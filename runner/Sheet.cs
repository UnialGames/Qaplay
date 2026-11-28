using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Qaplay.Runner
{
    /// <summary>
    /// 시트 설정(기본 qaplay-sheet.json). 어디에 있는 어떤 시트인지, 칸 이름과 적을 말을 팀의 양식에 맞춰 정한다.
    /// 경로는 이 설정 파일이 있는 폴더 기준이다.
    /// </summary>
    sealed class SheetConfig
    {
        public const string DefaultFile = "qaplay-sheet.json";

        /// <summary>
        /// Type은 csv 또는 xlsx. csv에서는 Tc와 Summary가 파일이고, xlsx에서는 File이 통합 문서이고 Tc와 Summary가 그 안의 탭 이름이다.
        /// </summary>
        public string Folder, Type, File, Tc, Summary;
        public readonly List<string> Rules = new List<string>();

        // 칸 이름. 설정의 columns로 바꾼다. 앞의 아홉은 TC 표, 뒤의 여덟은 요약 표다
        readonly Dictionary<string, string> m_Columns = new Dictionary<string, string>
        {
            ["tc"] = "TC", ["content"] = "Steps", ["expected"] = "Expected",
            ["rule"] = "Rule", ["result"] = "Result", ["run"] = "Last run", ["bugs"] = "Bugs", ["report"] = "Report", ["basis"] = "Rule basis",
            ["build"] = "Build", ["time"] = "Time", ["device"] = "Device",
            ["total"] = "Total", ["passed"] = "Passed", ["failed"] = "Failed", ["flaky"] = "Flaky", ["rate"] = "Pass rate",
        };

        // 칸에 적는 말. 설정의 labels로 바꾼다
        readonly Dictionary<string, string> m_Labels = new Dictionary<string, string>
        {
            ["passed"] = "passed", ["failed"] = "failed", ["flaky"] = "flaky", ["notRun"] = "not run",
            ["ruleOk"] = "ok", ["ruleMissing"] = "missing", ["ruleOutdated"] = "outdated",
        };

        public string Column(string key) => m_Columns[key];
        public string Label(string key) => m_Labels[key];

        /// <summary>--sheet로 준 설정, 없으면 실행한 폴더의 qaplay-sheet.json. 그것도 없으면 null(시트를 쓰지 않는다).</summary>
        public static SheetConfig Load(Args args)
        {
            string file = args.Get("sheet", null);
            if (file == null && !System.IO.File.Exists(DefaultFile)) return null;
            file = file ?? DefaultFile;
            if (!System.IO.File.Exists(file)) throw new UsageException("no such sheet settings file: " + file);
            try
            {
                var json = (Dictionary<string, object>)Json.Parse(System.IO.File.ReadAllText(file));
                var config = new SheetConfig
                {
                    Folder = Path.GetDirectoryName(Path.GetFullPath(file)),
                    Type = json.TryGetValue("type", out object type) ? (string)type : "csv",
                    File = json.TryGetValue("file", out object workbook) ? (string)workbook : null,
                    Tc = json.TryGetValue("tc", out object tc) ? (string)tc : null,
                    Summary = json.TryGetValue("summary", out object summary) ? (string)summary : null,
                };
                if (json.TryGetValue("rules", out object rules))
                    foreach (object folder in (List<object>)rules) config.Rules.Add((string)folder);
                Override(config.m_Columns, json, "columns");
                Override(config.m_Labels, json, "labels");
                if (config.Type != "csv" && config.Type != "xlsx") throw new FormatException("type must be csv or xlsx");
                if (config.Type == "xlsx" && config.File == null) throw new FormatException("an xlsx sheet needs \"file\": the workbook");
                if (config.Tc == null) throw new FormatException(config.Type == "xlsx" ? "\"tc\" is missing: the name of the tab with the test cases" : "\"tc\" is missing: the CSV file with the test cases");
                return config;
            }
            catch (Exception e) when (e is FormatException || e is InvalidCastException || e is KeyNotFoundException)
            {
                throw new UsageException("cannot read " + file + ": " + e.Message);
            }
        }

        static void Override(Dictionary<string, string> target, Dictionary<string, object> json, string field)
        {
            if (!json.TryGetValue(field, out object value)) return;
            foreach (KeyValuePair<string, object> pair in (Dictionary<string, object>)value)
            {
                // 모르는 이름을 조용히 넘기면 오타 난 설정이 적용되지 않은 채로 돈다
                if (!target.ContainsKey(pair.Key)) throw new FormatException("unknown name in " + field + ": " + pair.Key);
                target[pair.Key] = (string)pair.Value;
            }
        }

        public string PathOf(string relative) => Path.GetFullPath(Path.Combine(Folder, relative));
    }

    /// <summary>
    /// 표를 읽고 쓰는 곳. 표는 줄의 목록이고 줄은 칸(글자)의 목록이다. 첫 줄이 칸 이름이다.
    /// 지금은 CSV 파일뿐이다. Excel과 Google Sheets는 같은 자리에 더한다.
    /// </summary>
    abstract class SheetStore
    {
        /// <summary>which: "tc" 또는 "summary". 아직 없는 표는 빈 목록.</summary>
        public abstract List<List<string>> Read(string which);
        public abstract void Write(string which, List<List<string>> rows);
        /// <summary>사람에게 보여 줄 그 표의 위치.</summary>
        public abstract string Where(string which);

        public static SheetStore Open(SheetConfig config) => config.Type == "xlsx" ? new XlsxSheet(config) : (SheetStore)new CsvSheet(config);
    }

    /// <summary>
    /// Excel 파일 하나: TC 표와 요약 표가 탭으로 들어 있다. ClosedXML로 읽고 쓴다.
    /// 쓸 때는 글자가 바뀐 칸만 고친다. 사람이 적은 칸의 값과 서식(굵기, 색, 칸 너비)은 그대로 남는다.
    /// </summary>
    // ponytail: 파일을 통째로 읽어 다시 저장한다. ClosedXML이 다루지 못하는 것(일부 차트, 피벗, 매크로)이 든 파일은
    // 저장할 때 그 부분이 빠질 수 있다. TC 전용 파일을 쓰게 안내한다
    sealed class XlsxSheet : SheetStore
    {
        readonly SheetConfig m_Config;

        public XlsxSheet(SheetConfig config)
        {
            m_Config = config;
        }

        string FilePath => m_Config.PathOf(m_Config.File);
        string Tab(string which) => which == "tc" ? m_Config.Tc : m_Config.Summary;

        public override string Where(string which) => Tab(which) == null ? null : FilePath + " [" + Tab(which) + "]";

        // Excel이 열어 둔 파일도 읽을 수는 있게, 다른 프로그램의 쓰기를 허용하며 연다.
        // 파일은 메모리로 다 읽어 들인다: ClosedXML은 저장할 때 처음 읽은 흐름을 다시 쓰므로 그때까지 살아 있어야 한다
        ClosedXML.Excel.XLWorkbook Load()
        {
            var memory = new MemoryStream();
            using (var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) stream.CopyTo(memory);
            memory.Position = 0;
            return new ClosedXML.Excel.XLWorkbook(memory);
        }

        public override List<List<string>> Read(string which)
        {
            var rows = new List<List<string>>();
            if (Tab(which) == null || !System.IO.File.Exists(FilePath)) return rows;
            using ClosedXML.Excel.XLWorkbook workbook = Load();
            if (!workbook.TryGetWorksheet(Tab(which), out ClosedXML.Excel.IXLWorksheet sheet)) return rows;
            int lastRow = sheet.LastRowUsed()?.RowNumber() ?? 0, lastColumn = sheet.LastColumnUsed()?.ColumnNumber() ?? 0;
            for (int r = 1; r <= lastRow; r++)
            {
                var row = new List<string>();
                // 화면에 보이는 글자로 읽는다(날짜나 숫자 서식이 적용된 모습)
                for (int c = 1; c <= lastColumn; c++) row.Add(Text(sheet.Cell(r, c)));
                rows.Add(row);
            }
            return rows;
        }

        // 수식은 계산된 값으로 읽는다. Excel이 저장한 파일에는 그 값이 들어 있지만, 다른 도구가 만든 파일에는 없을 수 있어 그때는 계산한다.
        // 계산하지 못하는 수식(지원하지 않는 함수 등)은 들어 있던 값 그대로 둔다
        static string Text(ClosedXML.Excel.IXLCell cell)
        {
            if (cell.HasFormula && cell.CachedValue.IsBlank)
                try { cell.InvalidateFormula(); _ = cell.Value; } catch (Exception) { }
            return cell.GetFormattedString();
        }

        public override void Write(string which, List<List<string>> rows)
        {
            bool exists = System.IO.File.Exists(FilePath);
            using ClosedXML.Excel.XLWorkbook workbook = exists ? Load() : new ClosedXML.Excel.XLWorkbook();
            if (!workbook.TryGetWorksheet(Tab(which), out ClosedXML.Excel.IXLWorksheet sheet)) sheet = workbook.AddWorksheet(Tab(which));
            for (int r = 0; r < rows.Count; r++)
            for (int c = 0; c < rows[r].Count; c++)
            {
                ClosedXML.Excel.IXLCell cell = sheet.Cell(r + 1, c + 1);
                string text = rows[r][c] ?? "";
                // 보이는 글자가 같으면 건드리지 않는다. 사람이 적은 칸은 값의 종류(숫자, 날짜)와 서식까지 그대로 남는다
                if (Text(cell) == text) continue;
                // 개수 같은 것은 Excel에서 셈할 수 있게 숫자로 적는다. 0으로 시작하는 글자(번호)는 글자로 둔다
                if (text.Length > 0 && text.Length < 10 && (text == "0" || text[0] != '0') && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int number))
                    cell.Value = number;
                else cell.Value = text;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            // 옆에 다 쓴 뒤에 바꿔치기한다: 쓰다가 죽어도 사람의 통합 문서가 반쯤 쓰인 채로 남지 않는다
            string temp = Path.ChangeExtension(FilePath, ".qaplay-tmp" + Path.GetExtension(FilePath));
            try
            {
                workbook.SaveAs(temp);
                System.IO.File.Move(temp, FilePath, true);
            }
            catch (UnauthorizedAccessException)
            {
                // Excel이 열어 둔 파일은 바꿔치기가 거부된다. 원본은 그대로다
                throw new IOException("cannot write " + FilePath + ": it is open in another program (Excel?) or read-only");
            }
            finally
            {
                System.IO.File.Delete(temp);
            }
        }
    }

    /// <summary>CSV 파일 둘: TC 표 하나, 요약 표 하나.</summary>
    sealed class CsvSheet : SheetStore
    {
        readonly SheetConfig m_Config;

        public CsvSheet(SheetConfig config)
        {
            m_Config = config;
        }

        public override string Where(string which)
        {
            string file = which == "tc" ? m_Config.Tc : m_Config.Summary;
            return file == null ? null : m_Config.PathOf(file);
        }

        public override List<List<string>> Read(string which)
        {
            string file = Where(which);
            return file != null && File.Exists(file) ? Csv.Parse(File.ReadAllText(file)) : new List<List<string>>();
        }

        public override void Write(string which, List<List<string>> rows)
        {
            string file = Where(which);
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            // Excel이 한글을 제대로 읽도록 BOM을 붙인다
            File.WriteAllText(file, Csv.Write(rows), new UTF8Encoding(true));
        }
    }

    /// <summary>CSV 읽고 쓰기(RFC 4180): 쉼표, 따옴표, 줄바꿈이 든 칸은 큰따옴표로 감싸고 따옴표는 두 번 적는다.</summary>
    static class Csv
    {
        public static List<List<string>> Parse(string text)
        {
            var rows = new List<List<string>>();
            var row = new List<string>();
            var cell = new StringBuilder();
            bool quoted = false, any = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (quoted)
                {
                    if (c != '"') cell.Append(c);
                    else if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                    else quoted = false;
                    continue;
                }
                if (c == '"') { quoted = true; any = true; }
                else if (c == ',') { row.Add(cell.ToString()); cell.Clear(); any = true; }
                else if (c == '\n' || c == '\r')
                {
                    if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                    // 빈 줄(파일 끝의 줄바꿈 포함)은 줄로 치지 않는다
                    if (any || cell.Length > 0) { row.Add(cell.ToString()); rows.Add(row); }
                    row = new List<string>();
                    cell.Clear();
                    any = false;
                }
                else cell.Append(c);
            }
            if (any || cell.Length > 0) { row.Add(cell.ToString()); rows.Add(row); }
            return rows;
        }

        public static string Write(List<List<string>> rows)
        {
            var text = new StringBuilder();
            foreach (List<string> row in rows)
            {
                for (int i = 0; i < row.Count; i++)
                {
                    if (i > 0) text.Append(',');
                    string cell = row[i] ?? "";
                    if (cell.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) text.Append(cell);
                    else text.Append('"').Append(cell.Replace("\"", "\"\"")).Append('"');
                }
                text.Append("\r\n");
            }
            return text.ToString();
        }
    }

    /// <summary>TC 한 줄을 읽은 것.</summary>
    sealed class TcRow
    {
        public string id;
        /// <summary>칸 이름과 그 칸의 글자(사람이 적은 칸과 도구가 채운 칸 모두).</summary>
        public Dictionary<string, object> cells = new Dictionary<string, object>();
        /// <summary>이 TC를 실행하는 규칙 파일들.</summary>
        public List<string> rules = new List<string>();
        /// <summary>ok, missing, outdated.</summary>
        public string ruleStatus;
    }

    /// <summary>
    /// 시트의 TC 표와 요약 표를 다룬다. 도구는 자기 칸(규칙 상태, 최신 결과, 최신 실행, 버그 리포트, 리포트, 규칙 기준)만 고치고
    /// 사람이 적은 칸은 건드리지 않는다. 도구의 칸이 시트에 없으면 맨 뒤에 더한다.
    /// </summary>
    sealed class TcSheet
    {
        static readonly string[] ToolColumns = { "rule", "result", "run", "bugs", "report", "basis" };

        readonly SheetConfig m_Config;
        readonly SheetStore m_Store;

        public TcSheet(SheetConfig config)
        {
            m_Config = config;
            m_Store = SheetStore.Open(config);
        }

        public string Where => m_Store.Where("tc");

        /// <summary>TC 번호마다 그 TC를 실행하는 규칙 파일들. 설정의 rules 폴더들을 훑어 머리말의 tc를 읽는다.</summary>
        Dictionary<string, List<string>> RulesByTc()
        {
            var found = new Dictionary<string, List<string>>();
            foreach (string folder in m_Config.Rules)
            {
                string path = m_Config.PathOf(folder);
                if (!Directory.Exists(path)) throw new UsageException("no such rules folder (from the sheet settings): " + path);
                string[] files = Directory.GetFiles(path, "*.yaml", SearchOption.AllDirectories);
                Array.Sort(files, StringComparer.Ordinal);
                foreach (string file in files)
                {
                    Rule rule;
                    try { rule = RuleReader.Read(File.ReadAllText(file)); }
                    catch (FormatException) { continue; } // 문법이 틀린 규칙은 run이 알린다
                    if (rule.tc == null) continue;
                    if (!found.TryGetValue(rule.tc, out List<string> list)) found[rule.tc] = list = new List<string>();
                    list.Add(file);
                }
            }
            return found;
        }

        // 표를 읽어 칸 이름으로 찾을 수 있게 한다. 도구의 칸이 없으면 더한다
        List<List<string>> ReadTable(out Dictionary<string, int> at)
        {
            List<List<string>> rows = m_Store.Read("tc");
            if (rows.Count == 0) throw new UsageException("the TC sheet is empty or missing: " + Where);
            List<string> header = rows[0];
            at = new Dictionary<string, int>();
            foreach (string key in new[] { "tc", "content", "expected" })
                at[key] = header.IndexOf(m_Config.Column(key));
            if (at["tc"] < 0)
                throw new UsageException("the TC sheet has no column named \"" + m_Config.Column("tc") + "\" (set columns.tc in the sheet settings): " + Where);
            foreach (string key in ToolColumns)
            {
                at[key] = header.IndexOf(m_Config.Column(key));
                if (at[key] >= 0) continue;
                at[key] = header.Count;
                header.Add(m_Config.Column(key));
            }
            // 줄마다 칸 수가 다를 수 있다(뒤의 빈 칸을 적지 않은 CSV). 머리말만큼 채운다
            foreach (List<string> row in rows)
                while (row.Count < header.Count) row.Add("");
            return rows;
        }

        // 사람이 적은 내용의 지문. 규칙을 쓴 뒤에 내용이나 기대 결과가 바뀌었는지 가리는 데 쓴다
        static string Fingerprint(List<string> row, Dictionary<string, int> at)
        {
            var text = new StringBuilder();
            foreach (string key in new[] { "content", "expected" })
                if (at[key] >= 0) text.Append(row[at[key]].Trim()).Append('\n');
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()));
            return Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
        }

        // 규칙 상태를 정한다. 처음 보는 TC는 지금의 내용을 기준으로 삼는다
        string RuleStatus(List<string> row, Dictionary<string, int> at, Dictionary<string, List<string>> rules)
        {
            if (!rules.ContainsKey(row[at["tc"]].Trim())) return "missing";
            if (row[at["basis"]].Length == 0) row[at["basis"]] = Fingerprint(row, at);
            return row[at["basis"]] == Fingerprint(row, at) ? "ok" : "outdated";
        }

        static string LabelOf(string status) => status == "ok" ? "ruleOk" : status == "missing" ? "ruleMissing" : "ruleOutdated";

        /// <summary>TC 목록. 아무것도 쓰지 않는다.</summary>
        public List<TcRow> List()
        {
            List<List<string>> rows = ReadTable(out Dictionary<string, int> at);
            Dictionary<string, List<string>> rules = RulesByTc();
            var list = new List<TcRow>();
            for (int n = 1; n < rows.Count; n++)
            {
                string id = rows[n][at["tc"]].Trim();
                if (id.Length == 0) continue;
                var tc = new TcRow { id = id, ruleStatus = RuleStatus(new List<string>(rows[n]), at, rules) };
                for (int c = 0; c < rows[0].Count; c++) tc.cells[rows[0][c]] = rows[n][c];
                if (rules.TryGetValue(id, out List<string> files)) tc.rules = files;
                list.Add(tc);
            }
            return list;
        }

        /// <summary>
        /// 실행 결과를 적는다. 모든 TC의 규칙 상태를 고치고, 이번에 실행한 TC에는 최신 결과와 실행 정보를, 요약 표에는 한 줄을 더한다.
        /// outcomes가 null이면 규칙 상태만 고친다. accept에 적힌 TC는 지금의 내용을 규칙의 기준으로 삼는다.
        /// 돌려주는 것은 사람에게 보여 줄 줄들이다.
        /// </summary>
        public List<string> Record(List<Outcome> outcomes, string build, string device, ICollection<string> accept = null)
        {
            var notes = new List<string>();
            List<List<string>> rows = ReadTable(out Dictionary<string, int> at);
            Dictionary<string, List<string>> rules = RulesByTc();
            string time = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

            // 이번에 실행한 규칙을 TC별로 모은다. 같은 TC의 규칙이 여럿이면 하나라도 실패하면 실패다
            var ran = new Dictionary<string, List<Outcome>>();
            if (outcomes != null)
                foreach (Outcome outcome in outcomes)
                {
                    if (outcome.rule?.tc == null) continue;
                    if (!ran.TryGetValue(outcome.rule.tc, out List<Outcome> list)) ran[outcome.rule.tc] = list = new List<Outcome>();
                    list.Add(outcome);
                }

            var seen = new HashSet<string>();
            int updated = 0;
            for (int n = 1; n < rows.Count; n++)
            {
                List<string> row = rows[n];
                string id = row[at["tc"]].Trim();
                if (id.Length == 0) continue;
                seen.Add(id);
                if (accept != null && accept.Contains(id)) row[at["basis"]] = Fingerprint(row, at);
                row[at["rule"]] = m_Config.Label(LabelOf(RuleStatus(row, at, rules)));

                if (!ran.TryGetValue(id, out List<Outcome> results)) continue;
                string status = results.Exists(o => o.Status == "failed") ? "failed" : results.Exists(o => o.Status == "flaky") ? "flaky" : "passed";
                row[at["result"]] = m_Config.Label(status);
                row[at["run"]] = build + " / " + device + " / " + time;
                // 이번 실행에서 버그 리포트로 올라간 것을 덧붙인다. 이미 적혀 있는 번호는 다시 적지 않는다
                foreach (Outcome outcome in results)
                    if (outcome.bugId != null && !("," + row[at["bugs"]].Replace(" ", "") + ",").Contains(",BUG-" + outcome.bugId + ","))
                        row[at["bugs"]] += (row[at["bugs"]].Length > 0 ? ", " : "") + "BUG-" + outcome.bugId;
                updated++;
            }
            if (accept != null)
                foreach (string id in accept)
                    if (!seen.Contains(id)) throw new UsageException("the sheet has no TC " + id + ": " + Where);
            m_Store.Write("tc", rows);
            if (outcomes == null) return notes;

            notes.Add("sheet: " + updated + " TC(s) updated in " + Where);
            foreach (string id in ran.Keys)
                if (!seen.Contains(id)) notes.Add("sheet: " + id + " is not in the sheet (the rule says tc: " + id + ")");

            if (m_Store.Where("summary") != null)
            {
                List<List<string>> summary = m_Store.Read("summary");
                string[] keys = { "build", "time", "device", "total", "passed", "failed", "flaky", "rate" };
                if (summary.Count == 0) summary.Add(new List<string>(Array.ConvertAll(keys, m_Config.Column)));
                int passed = outcomes.FindAll(o => o.Status == "passed").Count, flaky = outcomes.FindAll(o => o.Status == "flaky").Count;
                int failed = outcomes.Count - passed - flaky;
                // 불안정은 다시 돌려 통과한 것이므로 통과율에 통과로 센다
                string rate = outcomes.Count == 0 ? "" : ((passed + flaky) * 100.0 / outcomes.Count).ToString("0.#", CultureInfo.InvariantCulture) + "%";
                summary.Add(new List<string>
                {
                    build, time, device, outcomes.Count.ToString(CultureInfo.InvariantCulture), passed.ToString(CultureInfo.InvariantCulture),
                    failed.ToString(CultureInfo.InvariantCulture), flaky.ToString(CultureInfo.InvariantCulture), rate,
                });
                m_Store.Write("summary", summary);
                notes.Add("sheet: a summary row added to " + m_Store.Where("summary"));
            }
            return notes;
        }
    }

    /// <summary>qaplay tc ...: 시트의 TC를 보고(규칙을 쓸 때 읽는다), 규칙 상태를 맞춘다.</summary>
    static class TcCommands
    {
        const string Usage =
            "usage:\n" +
            "  qaplay tc list                 list the test cases of the sheet with their rule status and latest result\n" +
            "  qaplay tc show <TC>            show one test case in full (what a rule for it has to check)\n" +
            "  qaplay tc sync                 write the rule status of every test case to the sheet (run does this too)\n" +
            "  qaplay tc accept <TC>...       the rule was updated to the changed test case: clear \"outdated\"\n" +
            "  [--sheet FILE]                 the sheet settings (default " + SheetConfig.DefaultFile + " in the current folder)\n" +
            "  [--json]                       list, show: print the result as JSON on stdout";

        public static int Run(Args args)
        {
            string command = args.Paths.Count > 0 ? args.Paths[0] : "";
            if (Array.IndexOf(new[] { "list", "show", "sync", "accept" }, command) < 0)
            {
                Console.Error.WriteLine(Usage);
                return 2;
            }
            SheetConfig config = SheetConfig.Load(args)
                                 ?? throw new UsageException("no sheet settings: pass --sheet FILE or put " + SheetConfig.DefaultFile + " in this folder");
            var sheet = new TcSheet(config);
            switch (command)
            {
                case "list":
                {
                    List<TcRow> list = sheet.List();
                    foreach (TcRow tc in list)
                        Console.WriteLine(tc.id + "  rule: " + tc.ruleStatus + "  result: " + Text(tc, config, "result", config.Label("notRun"))
                                          + "  " + Text(tc, config, "content", "").Replace("\n", " "));
                    Console.WriteLine(list.Count + " test case(s) in " + sheet.Where);
                    if (args.Json) Program.PrintJson(new Dictionary<string, object> { ["sheet"] = sheet.Where, ["cases"] = list.ConvertAll(Data) });
                    return 0;
                }
                case "show":
                {
                    if (args.Paths.Count != 2) throw new UsageException("show needs exactly one TC");
                    TcRow tc = sheet.List().Find(t => t.id == args.Paths[1])
                               ?? throw new UsageException("the sheet has no TC " + args.Paths[1] + ": " + sheet.Where);
                    foreach (KeyValuePair<string, object> cell in tc.cells) Console.WriteLine(cell.Key + ": " + cell.Value);
                    Console.WriteLine("rule: " + tc.ruleStatus + (tc.rules.Count > 0 ? " (" + string.Join(", ", tc.rules) + ")" : ""));
                    if (args.Json) Program.PrintJson(Data(tc));
                    return 0;
                }
                case "sync":
                    sheet.Record(null, null, null);
                    Console.WriteLine("rule status written to " + sheet.Where);
                    return 0;
                default: // accept
                    if (args.Paths.Count < 2) throw new UsageException("accept needs a TC");
                    sheet.Record(null, null, null, args.Paths.GetRange(1, args.Paths.Count - 1));
                    Console.WriteLine("accepted: " + string.Join(", ", args.Paths.GetRange(1, args.Paths.Count - 1)));
                    return 0;
            }
        }

        static string Text(TcRow tc, SheetConfig config, string key, string fallback) =>
            tc.cells.TryGetValue(config.Column(key), out object value) && ((string)value).Length > 0 ? (string)value : fallback;

        static object Data(TcRow tc) => new Dictionary<string, object>
        {
            ["tc"] = tc.id, ["rule"] = tc.ruleStatus, ["rules"] = tc.rules, ["cells"] = tc.cells,
        };
    }
}
