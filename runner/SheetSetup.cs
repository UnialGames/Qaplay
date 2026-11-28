using System;
using System.Collections.Generic;
using System.IO;

namespace Qaplay.Runner
{
    /// <summary>
    /// qaplay tc init: 시트를 잇는 준비를 물어 가며 대신 한다. 어떤 종류의 시트든 순서는 같다:
    /// 종류를 고르고, 그 종류가 자기 시트를 찾고(ISheetProvider.Locate), 표와 칸을 정하고, 설정 파일을 쓰고, 실제로 적어 본다.
    /// 종류마다 다른 것은 "시트가 어디에 있고 어떻게 닿는가"뿐이고, 그것은 종류가 안다.
    /// 물어보는 값은 옵션으로도 줄 수 있다. Unity의 설정 창(qaplay > Sheet)이 그렇게 부른다.
    /// </summary>
    static class SheetSetup
    {
        // 팀의 시트에서 흔히 쓰는 칸 이름. 맞는 것이 있으면 묻지 않는다
        static readonly Dictionary<string, string[]> Aliases = new Dictionary<string, string[]>
        {
            ["tc"] = new[] { "TC", "TC 번호", "TC번호", "TC ID", "TC No", "TC No.", "ID", "No", "No.", "번호", "케이스 번호", "Test case", "Case" },
            ["content"] = new[] { "Steps", "Step", "내용", "스텝", "절차", "테스트 내용", "테스트 스텝", "Test steps", "Description" },
            ["expected"] = new[] { "Expected", "Expected result", "기대 결과", "기대결과", "예상 결과", "예상결과" },
        };

        static readonly Dictionary<string, string> What = new Dictionary<string, string>
        {
            ["tc"] = "the test case number", ["content"] = "what to do (the steps)", ["expected"] = "the expected result",
        };

        // 한국어를 고르면 도구가 더하는 칸의 이름과 칸에 적는 말도 한국어로 한다
        static readonly Dictionary<string, object> KoreanColumns = new Dictionary<string, object>
        {
            ["rule"] = "규칙 상태", ["result"] = "최신 결과", ["run"] = "최신 실행", ["bugs"] = "버그 리포트", ["report"] = "리포트", ["basis"] = "규칙 기준",
            ["build"] = "빌드", ["time"] = "실행 일시", ["device"] = "기기",
            ["total"] = "전체", ["passed"] = "통과", ["failed"] = "실패", ["flaky"] = "불안정", ["rate"] = "통과율",
        };

        static readonly Dictionary<string, object> KoreanLabels = new Dictionary<string, object>
        {
            ["passed"] = "통과", ["failed"] = "실패", ["flaky"] = "불안정", ["notRun"] = "미실행",
            ["ruleOk"] = "있음", ["ruleMissing"] = "없음", ["ruleOutdated"] = "갱신 필요",
        };

        // 어떤 종류의 시트인지: --type, 없으면 다른 옵션에서 짐작하고(주소를 줬으면 Google, 파일을 줬으면 그 확장자), 그래도 모르면 묻는다
        static ISheetProvider Kind(Args args, SheetProviders providers, Func<string, string> ask, Action<string> say)
        {
            string type = args.Get("type", null);
            if (type == null && args.Get("spreadsheet", null) != null) type = "google";
            if (type == null && args.Get("file", null) is string file)
                type = file.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ? "csv" : file.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) ? "xlsx" : null;
            if (type == null)
            {
                var names = new List<string>();
                foreach (ISheetProvider provider in providers.All) names.Add(provider.Name);
                int? picked = Choose(ask, say, "Which kind of sheet holds the test cases", names, false);
                if (picked == null) throw new UsageException("nobody is here to answer: give --type " + string.Join("|", TypesOf(providers)));
                return providers.All[picked.Value];
            }
            try
            {
                return providers.Get(type);
            }
            catch (FormatException e)
            {
                throw new UsageException("--" + e.Message);
            }
        }

        static List<string> TypesOf(SheetProviders providers)
        {
            var types = new List<string>();
            foreach (ISheetProvider provider in providers.All) types.Add(provider.Type);
            return types;
        }

        /// <param name="ask">물음을 보여 주고 답 한 줄을 받는다. 답할 사람이 없으면 null.</param>
        /// <summary>--settings로 고른(없으면 실행한 폴더의) 프로젝트 설정에 시트를 잇는다.</summary>
        public static int Run(Args args, SheetProviders providers, Func<string, string> ask, Action<string> say) =>
            Run(ProjectSettings.Load(args), args, providers, ask, say);

        /// <param name="settings">시트를 적어 넣을 프로젝트 설정. 아직 파일이 없으면 만든다.</param>
        public static int Run(ProjectSettings settings, Args args, SheetProviders providers, Func<string, string> ask, Action<string> say)
        {
            if (settings.Sheet != null && !args.Replace)
                throw new UsageException("a sheet is already connected in " + settings.File + ": give --replace to connect another one in its place");
            string folder = settings.Folder;
            var context = new SetupContext(args, ask, say, folder);

            // 1. 종류, 그리고 그 종류가 자기 시트를 찾는다
            ISheetProvider provider = Kind(args, providers, ask, say);
            SheetSource source = provider.Locate(context);

            // 2. TC가 있는 표(탭). 하나뿐이면 묻지 않는다
            List<string> names = source.tables.ConvertAll(t => t.name);
            string tab = args.Get("tab", null);
            if (tab != null && !names.Contains(tab)) throw new UsageException("the sheet has no tab named " + tab + " (it has: " + string.Join(", ", names) + ")");
            SheetTable table = tab != null ? source.tables.Find(t => t.name == tab)
                : source.tables.Count == 1 ? source.tables[0]
                : source.tables[Choose(ask, say, "Which tab has the test cases", names, false) ?? 0];
            say("test cases are in: " + table.name);

            // 3. 말의 언어: 칸 이름에 한글이 있으면 한국어, 빈 표면 묻는다
            List<string> header = table.header;
            bool empty = header.TrueForAll(string.IsNullOrWhiteSpace);
            string lang = args.Get("lang", null);
            if (lang == null)
            {
                if (header.Exists(Korean) || Korean(table.name)) lang = "ko";
                else if (empty) lang = (ask("Language of the columns and words the tool writes: en or ko [en]") ?? "").Trim().ToLowerInvariant();
                if (string.IsNullOrEmpty(lang)) lang = "en";
            }
            if (lang != "en" && lang != "ko") throw new UsageException("--lang must be en or ko");

            // 4. 칸: 비어 있으면 칸 이름을 적어 줄 것이고, 있으면 어느 칸이 무엇인지 찾는다
            var columns = new Dictionary<string, object>();
            string[] fresh = lang == "ko" ? new[] { "TC 번호", "내용", "기대 결과" } : new[] { "TC", "Steps", "Expected" };
            if (empty)
            {
                columns["tc"] = fresh[0];
                columns["content"] = fresh[1];
                columns["expected"] = fresh[2];
            }
            else
            {
                foreach (string name in new[] { "tc", "content", "expected" })
                {
                    // 설정 화면이 고른 칸(--tc-column 등)이 먼저다. 없으면 흔한 이름에서 찾고, 그래도 없으면 묻는다
                    string found = args.Get(name + "-column", null);
                    if (found != null && !header.Contains(found))
                        throw new UsageException(table.name + " has no column named " + found + " (it has: " + string.Join(", ", header) + ")");
                    found ??= Suggest(header, name);
                    if (found == null)
                    {
                        int? picked = Choose(ask, say, "Which column has " + What[name], header, name != "tc");
                        if (picked == null && name == "tc")
                            throw new UsageException("cannot tell which column has the test case number: name it TC, or give --tc-column NAME");
                        found = picked == null ? null : header[picked.Value];
                    }
                    if (found == null) continue;
                    columns[name] = found;
                    say("column with " + What[name] + ": " + found);
                }
            }

            // 5. 규칙 파일의 폴더: 규칙 상태(있음, 없음)를 정하는 데 쓴다. 프로젝트 설정에 이미 있으면 그것을 쓰고 묻지 않는다
            List<string> rules = settings.RulesAsWritten;
            bool newRules = rules.Count == 0;
            if (newRules)
            {
                string given = args.Get("rules", null);
                if (given != null && !Directory.Exists(given)) throw new UsageException("no such rules folder: " + given);
                while (given == null)
                {
                    given = (ask("Folder with the rule files (Enter if there is none yet)") ?? "").Trim().Trim('"');
                    if (given.Length == 0 || Directory.Exists(given)) break;
                    say("no such folder: " + Path.GetFullPath(given));
                    given = null;
                }
                if (given.Length > 0) rules.Add(context.Stored(given));
            }

            // 6. 시트 설정. 종류가 준 값(file, spreadsheet 등)에 공통인 것을 더한다. 비밀은 들어가지 않는다: 저장소에 올려도 되는 파일이다
            var config = new Dictionary<string, object>(source.settings)
            {
                ["type"] = provider.Type, ["tc"] = table.name, ["summary"] = provider.SummaryName(table.name, lang),
            };
            if (lang == "ko")
            {
                foreach (KeyValuePair<string, object> pair in KoreanColumns) columns[pair.Key] = pair.Value;
                config["labels"] = KoreanLabels;
            }
            config["columns"] = columns;
            SheetConfig parsed = SheetConfig.Parse(config, folder, rules, providers);

            // 빈 표면 칸 이름을 적는다. 어떤 종류든 같은 길로 쓴다(파일이 없으면 이때 생긴다)
            if (empty)
            {
                provider.Open(parsed).Write("tc", new List<List<string>> { new List<string>(fresh) });
                say("it was empty: wrote the column names " + string.Join(", ", fresh) + ". Write your test cases under them.");
            }

            // 7. 실제로 적어 본다: 도구의 칸이 생기고, 쓸 수 있는지 여기서 드러난다. 설정은 이것이 된 뒤에 저장한다(안 되면 쓰던 설정이 그대로 남는다)
            TcSheet sheet = providers.Sheet(parsed);
            sheet.Record(null, null, null);
            say("checked: the tool's columns are in " + sheet.Where);

            settings.Set("sheet", config);
            if (newRules && rules.Count > 0) settings.Set("rules", rules.ConvertAll(folderName => (object)folderName));
            settings.Save();
            say("");
            say("settings written to " + settings.File + " (it holds no secret: keep it with the project)");
            say("");
            say("Done. Try: qaplay tc list");
            say("From now on qaplay run writes its results to the sheet when it is run in this folder.");
            return 0;
        }

        // 첫 줄의 칸 이름 가운데 그 뜻으로 흔히 쓰는 것. 없으면 null
        static string Suggest(List<string> header, string name) =>
            header.Find(cell => Array.Exists(Aliases[name], alias => alias.Equals(cell.Trim(), StringComparison.OrdinalIgnoreCase)));

        /// <summary>
        /// qaplay tc key [--credentials FILE]: 이 PC의 Google 키가 누구의 것인지 보여 준다. 파일을 주면 그것을 이 PC의 키로 넣는다.
        /// 설정 창이 부른다. 키가 없어도 실패가 아니다(창이 "아직 없음"을 보여 준다).
        /// </summary>
        public static int KeyCommand(Args args)
        {
            string given = args.Get("credentials", null);
            GoogleKey key = given != null ? GoogleProvider.Install(given) : GoogleProvider.Installed();
            Console.WriteLine(key == null
                ? "no Google key on this PC yet (it would be kept at " + GoogleKey.DefaultFile + ")"
                : "Google key: " + key.Email + " (" + key.File + ")");
            if (args.Json)
                Program.PrintJson(new Dictionary<string, object> { ["email"] = key?.Email, ["file"] = key?.File ?? GoogleKey.DefaultFile });
            return 0;
        }

        /// <summary>
        /// qaplay tc tabs: 그 시트에 닿는지 보고, 표(탭)마다 첫 줄의 칸 이름과 그 가운데 TC 번호, 내용, 기대 결과로 보이는 칸을 알려 준다.
        /// 어떤 종류든 같다. 닿지 못하면 까닭과 할 일을 알려 주고 종료 코드 1로 끝난다. 아무것도 만들거나 고치지 않는다.
        /// </summary>
        public static int TabsCommand(Args args, SheetProviders providers)
        {
            ISheetProvider provider = Kind(args, providers, question => null, line => { });
            var result = new Dictionary<string, object> { ["type"] = provider.Type };
            try
            {
                SheetSource source = provider.Locate(new SetupContext(args, question => null, line => { }, Directory.GetCurrentDirectory()));
                var tabs = new List<object>();
                foreach (SheetTable table in source.tables)
                {
                    var columns = new Dictionary<string, object>();
                    foreach (string name in new[] { "tc", "content", "expected" }) columns[name] = Suggest(table.header, name);
                    tabs.Add(new Dictionary<string, object>
                    {
                        ["title"] = table.name, ["header"] = table.header, ["columns"] = columns, ["korean"] = table.header.Exists(Korean) || Korean(table.name),
                    });
                    Console.WriteLine(table.name + ": " + (table.header.Count == 0 ? "(empty)" : string.Join(", ", table.header)));
                }
                result["ok"] = true;
                result["email"] = source.account;
                result["tabs"] = tabs;
                if (args.Json) Program.PrintJson(result);
                return 0;
            }
            catch (SheetAccessException e)
            {
                Console.WriteLine(e.Message);
                result["ok"] = false;
                result["status"] = e.Status;
                result["message"] = e.Message;
                result["hint"] = e.Hint;
                result["email"] = e.Account;
                if (args.Json) Program.PrintJson(result);
                return 1;
            }
        }

        // 번호로 고르게 한다. 고르지 않아도 되는 것이면 그냥 Enter로 넘어간다(null). 답할 사람이 없어도 null
        static int? Choose(Func<string, string> ask, Action<string> say, string question, List<string> options, bool optional)
        {
            for (int n = 0; n < options.Count; n++) say("  " + (n + 1) + ". " + options[n]);
            while (true)
            {
                string answer = ask(question + "? Type its number" + (optional ? " (Enter if there is none)" : ""));
                if (answer == null) return null;
                if (answer.Trim().Length == 0 && optional) return null;
                if (int.TryParse(answer.Trim(), out int number) && number >= 1 && number <= options.Count) return number - 1;
                say("type a number from 1 to " + options.Count);
            }
        }

        static bool Korean(string text)
        {
            foreach (char c in text)
                if (c >= '가' && c <= '힣') return true;
            return false;
        }
    }
}
