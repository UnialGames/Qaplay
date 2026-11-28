using System;
using System.Collections.Generic;
using System.IO;

namespace Qaplay.Runner
{
    /// <summary>
    /// 시트의 종류를 고르는 구조의 검사: 종류마다 준비(tc init)가 같은 길로 되는지, 종류를 하나 더 끼우면 나머지가 그대로 도는지.
    /// </summary>
    static class SheetKindsSelfTest
    {
        static void Check(bool condition, string what)
        {
            if (!condition) throw new InvalidOperationException("selftest failed: " + what);
        }

        /// <summary>검사용 종류: 표를 메모리에 둔다. 러너의 다른 코드는 이것이 무엇인지 모른 채 쓴다.</summary>
        sealed class MemoryProvider : ISheetProvider
        {
            public readonly Dictionary<string, List<List<string>>> tables = new Dictionary<string, List<List<string>>>();

            sealed class Store : SheetStore
            {
                readonly MemoryProvider m_Owner;
                readonly SheetConfig m_Config;

                public Store(MemoryProvider owner, SheetConfig config)
                {
                    m_Owner = owner;
                    m_Config = config;
                }

                string Name(string which) => which == "tc" ? m_Config.Tc : m_Config.Summary;
                public override string Where(string which) => Name(which) == null ? null : "memory:" + Name(which);

                public override List<List<string>> Read(string which) =>
                    Name(which) != null && m_Owner.tables.TryGetValue(Name(which), out List<List<string>> rows)
                        ? rows.ConvertAll(row => new List<string>(row))
                        : new List<List<string>>();

                public override void Write(string which, List<List<string>> rows) => m_Owner.tables[Name(which)] = rows.ConvertAll(row => new List<string>(row));
            }

            public string Type => "memory";
            public string Name => "In memory";

            public void Check(SheetConfig config)
            {
                if (config.Tc == null) throw new FormatException("\"tc\" is missing");
            }

            public SheetStore Open(SheetConfig config) => new Store(this, config);

            public SheetSource Locate(SetupContext context)
            {
                var source = new SheetSource();
                source.settings["note"] = "kept in memory";
                foreach (KeyValuePair<string, List<List<string>>> table in tables)
                    source.tables.Add(new SheetTable { name = table.Key, header = table.Value.Count > 0 ? new List<string>(table.Value[0]) : new List<string>() });
                return source;
            }

            public string SummaryName(string tc, string language) => tc + "-summary";
        }

        public static void Run()
        {
            string folder = Path.Combine(Path.GetTempPath(), "qaplay-selftest-" + Guid.NewGuid().ToString("N"));
            string was = Directory.GetCurrentDirectory();
            var said = new List<string>();
            try
            {
                Directory.CreateDirectory(Path.Combine(folder, "rules"));
                File.WriteAllText(Path.Combine(folder, "rules", "buy.yaml"), "tc: TC-1\n---\n- tapOn: 구매\n");
                // 사람이 프로젝트 폴더에서 명령을 치는 것처럼, 경로는 그 폴더 기준으로 준다
                Directory.SetCurrentDirectory(folder);
                SheetProviders providers = SheetProviders.BuiltIn();
                var ran = new Outcome { file = "buy.yaml", rule = RuleReader.Read("tc: TC-1\n---\n- tapOn: a\n"), passed = true };

                // ---- 종류를 모르는 값은 무엇을 쓸 수 있는지와 함께 거절한다 ----
                try { providers.Get("numbers"); Check(false, "an unknown kind is refused"); }
                catch (FormatException e) { Check(e.Message == "type must be csv, xlsx or google", "the refusal lists the kinds that exist"); }

                // ---- Excel: 없는 파일을 가리키면 만들어 준다 ----
                Check(SheetSetup.Run(new Args(new[] { "tc", "init", "--settings", "excel.json", "--type", "xlsx", "--file", "qa/cases.xlsx", "--lang", "ko", "--rules", "rules" }),
                    providers, question => null, said.Add) == 0, "an Excel sheet is set up with options only");
                SheetConfig excel = SheetConfig.Load("excel.json", providers);
                Check(excel.Type == "xlsx" && excel.File == "qa/cases.xlsx" && excel.Tc == "TC" && excel.Summary == "요약" && excel.Rules[0] == "rules"
                      && excel.Column("tc") == "TC 번호", "the settings keep paths relative to the settings file");
                List<SheetTable> made = XlsxSheet.Tables(Path.Combine(folder, "qa", "cases.xlsx"));
                Check(made.Count == 1 && made[0].name == "TC" && made[0].header[0] == "TC 번호" && made[0].header[3] == "규칙 상태", "the workbook is created with the column names");
                // 사람이 TC를 적은 뒤: 다른 탭이 생겨도 설정은 그대로 돈다
                var writer = providers.Get("xlsx").Open(excel);
                List<List<string>> rows = writer.Read("tc");
                rows.Add(new List<string> { "TC-1", "구매를 누른다", "골드가 100 준다" });
                writer.Write("tc", rows);
                providers.Sheet(excel).Record(new List<Outcome> { ran }, "1.2", "PC");
                TcRow first = providers.Sheet(excel).List()[0];
                Check(first.id == "TC-1" && first.ruleStatus == "ok" && first.cells["최신 결과"].Equals("통과"), "results are written to the Excel sheet");
                Console.SetOut(TextWriter.Null);
                try
                {
                    Check(SheetSetup.TabsCommand(new Args(new[] { "tc", "tabs", "--file", "qa/cases.xlsx" }), providers) == 0, "the tabs of a workbook can be listed (kind taken from the file name)");
                }
                finally
                {
                    Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
                }

                // ---- CSV: 팀이 쓰던 파일. 종류는 파일 이름에서 안다 ----
                File.WriteAllText("cases.csv", "ID,Area,Description,Expected result\r\nTC-1,Shop,Press buy,Gold goes down by 100\r\n");
                Check(SheetSetup.Run(new Args(new[] { "tc", "init", "--settings", "csv.json", "--file", "cases.csv", "--rules", "rules" }),
                    providers, question => null, said.Add) == 0, "a CSV sheet is set up; the kind is taken from the file name");
                SheetConfig csv = SheetConfig.Load("csv.json", providers);
                Check(csv.Type == "csv" && csv.Tc == "cases.csv" && csv.Summary == "summary.csv" && csv.Column("tc") == "ID" && csv.Column("content") == "Description"
                      && csv.Column("expected") == "Expected result" && csv.Label("passed") == "passed", "the columns of an existing CSV are found; English stays English");
                providers.Sheet(csv).Record(new List<Outcome> { ran }, "1.2", "PC");
                Check(File.ReadAllText("cases.csv").Contains("TC-1,Shop,Press buy,Gold goes down by 100,ok,passed,") && File.Exists("summary.csv"), "results are written to the CSV files");
                try
                {
                    SheetSetup.Run(new Args(new[] { "tc", "init", "--settings", "bad.json", "--type", "xlsx", "--file", "cases.csv" }), providers, question => null, said.Add);
                    Check(false, "a file of the wrong kind is refused");
                }
                catch (UsageException) { }

                // ---- 물어 가며: 종류를 고르고, 파일을 적는다 ----
                var asked = new List<string>();
                string Answer(string question)
                {
                    asked.Add(question);
                    if (question.StartsWith("Which kind")) return "1";
                    if (question.StartsWith("Path of the CSV file")) return "new/tc.csv";
                    if (question.StartsWith("Language")) return "";
                    if (question.StartsWith("Folder")) return "rules";
                    throw new InvalidOperationException("unexpected question: " + question);
                }
                Check(SheetSetup.Run(new Args(new[] { "tc", "init", "--settings", "asked.json" }), providers, Answer, said.Add) == 0, "init by answering: kind, then where");
                Check(said.Contains("  1. CSV file") && said.Contains("  2. Excel file (.xlsx)") && said.Contains("  3. Google Sheets"), "the kinds to choose from are the ones registered");
                Check(File.ReadAllText(Path.Combine("new", "tc.csv")).Contains("TC,Steps,Expected,Rule,Result") && SheetConfig.Load("asked.json", providers).Summary == "new/summary.csv",
                    "a new CSV gets its column names; the summary goes next to it");

                // ---- 종류를 하나 더 끼운다: 러너의 나머지 코드는 고치지 않는다 ----
                var memory = new MemoryProvider();
                memory.tables["cases"] = new List<List<string>> { new List<string> { "TC", "Steps", "Expected" }, new List<string> { "TC-1", "Press buy", "Gold -100" } };
                var extended = new SheetProviders(new CsvProvider(), memory);
                Check(SheetSetup.Run(new Args(new[] { "tc", "init", "--settings", "memory.json", "--type", "memory", "--rules", "rules" }), extended, question => null, said.Add) == 0,
                    "a kind that was plugged in is set up like the built-in ones");
                SheetConfig custom = SheetConfig.Load("memory.json", extended);
                extended.Sheet(custom).Record(new List<Outcome> { ran }, "1.2", "PC");
                Check(custom.Type == "memory" && custom.Summary == "cases-summary" && File.ReadAllText("memory.json").Contains("kept in memory")
                      && memory.tables["cases"][1][4] == "passed" && memory.tables["cases-summary"].Count == 2, "results go through the plugged-in kind");
                try { SheetConfig.Load("memory.json", providers); Check(false, "settings of a kind that is not registered are refused"); }
                catch (UsageException e) { Check(e.Message.Contains("type must be csv, xlsx or google"), "and the refusal says which kinds there are"); }
            }
            finally
            {
                Directory.SetCurrentDirectory(was);
                Directory.Delete(folder, true);
            }
        }
    }
}
