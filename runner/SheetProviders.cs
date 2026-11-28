using System;
using System.Collections.Generic;
using System.IO;

namespace Qaplay.Runner
{
    /// <summary>
    /// 시트의 종류 하나(전략). 그 종류의 시트를 어떻게 읽고 쓰는지와, 처음 이을 때 무엇을 받아야 하는지를 안다.
    /// TC 표를 다루는 쪽(TcSheet)과 준비 명령(SheetSetup)은 종류를 모른다. 새 종류는 이것을 하나 더 만들어 SheetProviders에 넣으면 된다.
    /// </summary>
    interface ISheetProvider
    {
        /// <summary>설정 파일의 type에 적는 이름.</summary>
        string Type { get; }
        /// <summary>사람에게 보여 주는 이름.</summary>
        string Name { get; }
        /// <summary>준비 명령에 --type 없이 준 옵션이 이 종류의 것으로 보이는지(파일의 확장자, 시트의 주소).</summary>
        bool Fits(Args args);
        /// <summary>설정이 이 종류에 맞는지. 모자라면 FormatException.</summary>
        void Check(SheetConfig config);
        /// <summary>표를 읽고 쓰는 곳.</summary>
        SheetStore Open(SheetConfig config);
        /// <summary>
        /// 준비: 이 종류의 시트가 어디에 있는지를 받아(옵션이나 물음으로) 닿는지 확인하고, 설정에 적을 값과 그 안의 표들을 돌려준다.
        /// 아무것도 만들거나 고치지 않는다(Google 키를 그 PC에 넣어 두는 것은 예외다).
        /// </summary>
        SheetSource Locate(SetupContext context);
        /// <summary>요약 표의 이름. TC 표의 이름과 고른 언어(en, ko)에서 정한다.</summary>
        string SummaryName(string tc, string language);
    }

    /// <summary>표 하나: 탭이나 파일. header는 첫 줄의 칸 이름이고, 비어 있으면 빈 표다.</summary>
    sealed class SheetTable
    {
        public string name;
        public List<string> header = new List<string>();
    }

    /// <summary>준비 과정에서 찾아낸 시트.</summary>
    sealed class SheetSource
    {
        /// <summary>설정 파일에 그대로 들어갈 값(type, tc, summary는 뺀 것: file, spreadsheet 등).</summary>
        public readonly Dictionary<string, object> settings = new Dictionary<string, object>();
        public readonly List<SheetTable> tables = new List<SheetTable>();
        /// <summary>시트를 공유해 줘야 하는 계정(Google의 서비스 계정 메일). 없는 종류는 null.</summary>
        public string account;
    }

    /// <summary>시트에 닿지 못했다. 까닭(message)과 사람이 할 일(Hint)을 함께 알린다.</summary>
    sealed class SheetAccessException : IOException
    {
        public readonly int Status;
        public readonly string Hint, Account;

        public SheetAccessException(int status, string message, string hint, string account) : base(message + " (" + hint + ")")
        {
            Status = status;
            Hint = hint;
            Account = account;
        }
    }

    /// <summary>준비 과정이 값을 얻는 길: 옵션으로 왔으면 그것, 아니면 사람에게 묻는다.</summary>
    sealed class SetupContext
    {
        public readonly Args args;
        /// <summary>물음을 보여 주고 답 한 줄을 받는다. 답할 사람이 없으면 null.</summary>
        public readonly Func<string, string> ask;
        public readonly Action<string> say;
        /// <summary>설정 파일이 놓일 폴더. 설정에 적는 경로는 이 폴더 기준이다.</summary>
        public readonly string folder;

        public SetupContext(Args args, Func<string, string> ask, Action<string> say, string folder)
        {
            this.args = args;
            this.ask = ask;
            this.say = say;
            this.folder = folder;
        }

        /// <summary>옵션의 값, 없으면 물어서 받은 값. 둘 다 없으면 어떤 옵션이 모자란지 알린다.</summary>
        public string Need(string option, string hint, string question)
        {
            string value = args.Get(option, null) ?? ask(question);
            if (value == null) throw new UsageException("nobody is here to answer: give --" + option + " " + hint);
            return value.Trim().Trim('"');
        }

        /// <summary>설정 파일에 적을 모습: 설정 파일의 폴더 안이면 그 폴더 기준의 경로, 밖이면 전체 경로.</summary>
        public string Stored(string path)
        {
            string full = Path.GetFullPath(path), relative = Path.GetRelativePath(folder, full);
            return (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? full : relative).Replace('\\', '/');
        }
    }

    /// <summary>
    /// 쓸 수 있는 시트의 종류들. 프로그램이 시작할 때 한 번 만들어(조립) 필요한 곳에 넘겨준다.
    /// 종류를 고르는 조건문은 여기에만 있다: 설정의 type으로 전략을 찾는다.
    /// </summary>
    sealed class SheetProviders
    {
        readonly List<ISheetProvider> m_All;
        readonly string m_ModulesFolder;

        public SheetProviders(params ISheetProvider[] providers) : this(null, providers)
        {
        }

        SheetProviders(string modulesFolder, IEnumerable<ISheetProvider> providers)
        {
            m_ModulesFolder = modulesFolder;
            m_All = new List<ISheetProvider>(providers);
        }

        /// <summary>코어에 든 CSV와, 설치된 모듈이 더하는 종류들(Excel 모듈의 xlsx, Google 모듈의 google).</summary>
        public static SheetProviders Installed(Modules modules)
        {
            var all = new List<ISheetProvider> { new CsvProvider() };
            all.AddRange(modules.Create<ISheetProvider>());
            return new SheetProviders(modules.Folder, all);
        }

        public IReadOnlyList<ISheetProvider> All => m_All;

        public ISheetProvider Get(string type)
        {
            ISheetProvider found = m_All.Find(provider => provider.Type == type);
            if (found != null) return found;
            List<string> names = m_All.ConvertAll(provider => provider.Type);
            string last = names[names.Count - 1];
            names.RemoveAt(names.Count - 1);
            throw new FormatException("type must be " + (names.Count == 0 ? last : string.Join(", ", names) + " or " + last)
                                      + (m_ModulesFolder == null ? "" : " (other kinds come as modules in " + m_ModulesFolder + ": see qaplay modules)"));
        }

        /// <summary>설정에 적힌 종류의 시트를 열어 TC 표를 다루는 것을 만든다.</summary>
        public TcSheet Sheet(SheetConfig config) => new TcSheet(config, Get(config.Type).Open(config));
    }

    /// <summary>CSV 파일: TC 표 파일 하나, 요약 표 파일 하나.</summary>
    sealed class CsvProvider : ISheetProvider
    {
        public string Type => "csv";
        public string Name => "CSV file";

        public bool Fits(Args args) => args.Get("file", null) is string file && file.EndsWith(".csv", StringComparison.OrdinalIgnoreCase);

        public void Check(SheetConfig config)
        {
            if (config.Tc == null) throw new FormatException("\"tc\" is missing: the CSV file with the test cases");
        }

        public SheetStore Open(SheetConfig config) => new CsvSheet(config);

        public SheetSource Locate(SetupContext context)
        {
            string path = context.Need("file", "FILE.csv", "Path of the CSV file with the test cases (it is created if it does not exist)");
            if (!path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) throw new UsageException("a CSV sheet is a .csv file: " + path);
            var source = new SheetSource();
            var table = new SheetTable { name = context.Stored(path) };
            if (File.Exists(path))
            {
                List<List<string>> rows = Csv.Parse(File.ReadAllText(path));
                if (rows.Count > 0) table.header = rows[0];
            }
            source.tables.Add(table);
            return source;
        }

        // 요약은 TC 파일 옆의 summary.csv다
        public string SummaryName(string tc, string language)
        {
            int slash = tc.LastIndexOf('/');
            return (slash < 0 ? "" : tc.Substring(0, slash + 1)) + "summary.csv";
        }
    }
}
