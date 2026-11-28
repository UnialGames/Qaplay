using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Qaplay.Runner
{
    /// <summary>
    /// qaplay tc init: Google Sheets를 쓰기까지의 준비를 물어 가며 대신 한다.
    /// 사람이 하는 일은 둘뿐이다: Google Cloud에서 키 파일을 내려받는 것, 시트를 그 키의 계정에 공유하는 것.
    /// 나머지(키 보관, 접근 확인, 탭과 칸 찾기, 설정 파일 쓰기, 쓰기 확인)는 이 명령이 한다.
    /// 물어보는 값은 옵션으로도 줄 수 있다(--credentials, --spreadsheet, --tab, --lang, --rules).
    /// </summary>
    static class SheetSetup
    {
        const string KeySteps =
            "A Google service account key is needed, once per PC. To get one:\n" +
            "  1. Open https://console.cloud.google.com/ and create a project (or pick one).\n" +
            "  2. APIs & Services > Library > search \"Google Sheets API\" > Enable.\n" +
            "  3. IAM & Admin > Service Accounts > Create service account. Any name; it needs no roles.\n" +
            "  4. Open that account > Keys > Add key > Create new key > JSON. A .json file is downloaded.\n" +
            "The same steps with more detail are in docs/sheet.md.";

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

        /// <param name="ask">물음을 보여 주고 답 한 줄을 받는다. 답할 사람이 없으면 null.</param>
        public static int Run(Args args, Func<string, string> ask, Action<string> say)
        {
            string file = args.Get("sheet", SheetConfig.DefaultFile);
            if (File.Exists(file))
                throw new UsageException(file + " is already here. To set up again, delete it first (or write the new settings elsewhere with --sheet FILE)");

            // 1. 키: 한 번 넣어 두면 그 PC에서는 다시 묻지 않는다
            GoogleKey key = Key(args, ask, say);
            var api = new GoogleApi(key);
            say("");
            say("The runner signs in to Google as: " + key.Email);
            say("Share your sheet with that address as an Editor (the Share button of the sheet), if you have not yet.");

            // 2. 시트: 닿을 때까지 무엇이 모자란지 알려 준다
            string given = args.Get("spreadsheet", null), spreadsheet = null;
            List<Dictionary<string, object>> tabs = null;
            while (tabs == null)
            {
                string text = given ?? ask("Address of the Google Sheet (copy it from the browser's address bar)") ?? throw Missing("--spreadsheet ADDRESS");
                try
                {
                    spreadsheet = GoogleSheet.IdOf(text);
                    tabs = api.Tabs(spreadsheet);
                }
                catch (Exception e) when (e is UsageException && args.Get("spreadsheet", null) == null || e is GoogleException google && (google.Status == 403 || google.Status == 404))
                {
                    say(e.Message);
                    if (!(e is GoogleException refused)) continue; // 주소가 아닌 것을 적었다: 다시 묻는다
                    say(GoogleSheet.Hint(refused.Status, key.Email, spreadsheet));
                    if (refused.Status == 404 && args.Get("spreadsheet", null) == null) continue;
                    // 공유하고 나서 이어 간다. 주소는 다시 묻지 않는다
                    given = text;
                    string answer = ask("Press Enter when that is done (or type q to stop)");
                    if (answer == null || answer.Trim() == "q") throw new UsageException("the sheet could not be opened: " + e.Message);
                }
            }

            // 3. TC가 있는 탭
            List<string> titles = tabs.ConvertAll(t => (string)t["title"]);
            string tab = args.Get("tab", null);
            if (tab != null && !titles.Contains(tab)) throw new UsageException("the sheet has no tab named " + tab + " (it has: " + string.Join(", ", titles) + ")");
            tab ??= titles.Count == 1 ? titles[0] : titles[Choose(ask, say, "Which tab has the test cases", titles, false) ?? 0];
            say("tab with the test cases: " + tab);

            // 4. 칸: 비어 있으면 칸 이름을 적어 주고, 있으면 어느 칸이 무엇인지 찾는다
            List<List<string>> rows = api.Values(spreadsheet, tab);
            List<string> header = rows.Count > 0 ? rows[0] : new List<string>();
            bool empty = header.TrueForAll(string.IsNullOrWhiteSpace);
            string lang = args.Get("lang", null);
            if (lang == null)
            {
                if (header.Exists(Korean) || Korean(tab)) lang = "ko";
                else if (empty) lang = (ask("Language of the columns and words the tool writes: en or ko [en]") ?? "").Trim().ToLowerInvariant();
                if (string.IsNullOrEmpty(lang)) lang = "en";
            }
            if (lang != "en" && lang != "ko") throw new UsageException("--lang must be en or ko");

            var columns = new Dictionary<string, object>();
            if (empty)
            {
                string[] names = lang == "ko" ? new[] { "TC 번호", "내용", "기대 결과" } : new[] { "TC", "Steps", "Expected" };
                var cells = new List<(int, int, object)>();
                for (int c = 0; c < names.Length; c++) cells.Add((0, c, names[c]));
                api.Update(spreadsheet, tab, cells);
                columns["tc"] = names[0];
                columns["content"] = names[1];
                columns["expected"] = names[2];
                say("the tab was empty: wrote the column names " + string.Join(", ", names) + ". Write your test cases under them.");
            }
            else
            {
                foreach (string name in new[] { "tc", "content", "expected" })
                {
                    // 설정 화면이 고른 칸(--tc-column 등)이 먼저다. 없으면 흔한 이름에서 찾고, 그래도 없으면 묻는다
                    string found = args.Get(name + "-column", null);
                    if (found != null && !header.Contains(found))
                        throw new UsageException("the tab " + tab + " has no column named " + found + " (it has: " + string.Join(", ", header) + ")");
                    found ??= Suggest(header, name);
                    if (found == null)
                    {
                        int? picked = Choose(ask, say, "Which column has " + What[name], header, name != "tc");
                        if (picked == null && name == "tc")
                            throw new UsageException("cannot tell which column has the test case number: name it TC, or run qaplay tc init where it can ask");
                        found = picked == null ? null : header[picked.Value];
                    }
                    if (found == null) continue;
                    columns[name] = found;
                    say("column with " + What[name] + ": " + found);
                }
            }

            // 5. 규칙 파일의 폴더: 규칙 상태(있음, 없음)를 정하는 데 쓴다
            string rules = args.Get("rules", null);
            if (rules != null && !Directory.Exists(rules)) throw new UsageException("no such rules folder: " + rules);
            while (rules == null)
            {
                rules = (ask("Folder with the rule files, relative to this folder (Enter if there is none yet)") ?? "").Trim().Trim('"');
                if (rules.Length == 0 || Directory.Exists(rules)) break;
                say("no such folder: " + Path.GetFullPath(rules));
                rules = null;
            }

            // 6. 설정 파일. 키의 경로는 적지 않는다: 저장소에 올려도 되는 파일이다
            var config = new Dictionary<string, object>
            {
                ["type"] = "google", ["spreadsheet"] = GoogleSheet.Address(spreadsheet), ["tc"] = tab, ["summary"] = lang == "ko" ? "요약" : "Summary",
                ["rules"] = rules.Length == 0 ? new List<object>() : new List<object> { rules.Replace('\\', '/') },
            };
            if (lang == "ko")
            {
                foreach (KeyValuePair<string, object> pair in KoreanColumns) columns[pair.Key] = pair.Value;
                config["labels"] = KoreanLabels;
            }
            config["columns"] = columns;
            File.WriteAllText(file, JsonSerializer.Serialize(config, new JsonSerializerOptions
            {
                WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }) + "\n", new UTF8Encoding(false));
            say("");
            say("settings written to " + Path.GetFullPath(file) + " (it holds no secret: keep it with the project)");

            // 7. 실제로 적어 본다: 도구의 칸이 생기고, 쓰기 권한이 있는지 여기서 드러난다
            try
            {
                var sheet = new TcSheet(SheetConfig.Load(file));
                sheet.Record(null, null, null);
                say("checked: the tool's columns are in " + sheet.Where);
            }
            catch (GoogleException e)
            {
                throw new GoogleException(e.Status, "the settings are written, but writing to the sheet failed: " + e.Message + ". Fix that, then run: qaplay tc sync");
            }
            say("");
            say("Done. Try: qaplay tc list");
            say("From now on qaplay run writes its results to the sheet when it is run in this folder.");
            return 0;
        }

        // 첫 줄의 칸 이름 가운데 그 뜻으로 흔히 쓰는 것. 없으면 null
        static string Suggest(List<string> header, string name) =>
            header.Find(cell => Array.Exists(Aliases[name], alias => alias.Equals(cell.Trim(), StringComparison.OrdinalIgnoreCase)));

        // 지금 쓰게 될 키. 없으면 null. 찾는 순서는 러너가 시트를 쓸 때와 같다
        static GoogleKey Installed()
        {
            string fromEnv = Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS");
            string file = !string.IsNullOrEmpty(fromEnv) && File.Exists(fromEnv) ? fromEnv : GoogleKey.DefaultFile;
            return File.Exists(file) ? GoogleKey.Read(file) : null;
        }

        /// <summary>
        /// qaplay tc key [--credentials FILE]: 이 PC의 Google 키가 누구의 것인지 보여 준다. 파일을 주면 그것을 이 PC의 키로 넣는다.
        /// 설정 화면(Unity의 qaplay > Google Sheet)이 부른다. 키가 없어도 실패가 아니다(화면이 "아직 없음"을 보여 준다).
        /// </summary>
        public static int KeyCommand(Args args)
        {
            string given = args.Get("credentials", null);
            if (given != null)
            {
                if (!File.Exists(given)) throw new UsageException("no such file: " + given);
                GoogleKey.Read(given); // 키가 아니면 여기서 거절한다. 틀린 파일로 있던 키를 덮어쓰지 않는다
                if (!Path.GetFullPath(given).Equals(Path.GetFullPath(GoogleKey.DefaultFile), StringComparison.OrdinalIgnoreCase))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(GoogleKey.DefaultFile));
                    File.Copy(given, GoogleKey.DefaultFile, true);
                }
            }
            GoogleKey key = given != null ? GoogleKey.Read(GoogleKey.DefaultFile) : Installed();
            Console.WriteLine(key == null
                ? "no Google key on this PC yet (it would be kept at " + GoogleKey.DefaultFile + ")"
                : "Google key: " + key.Email + " (" + key.File + ")");
            if (args.Json)
                Program.PrintJson(new Dictionary<string, object> { ["email"] = key?.Email, ["file"] = key?.File ?? GoogleKey.DefaultFile });
            return 0;
        }

        /// <summary>
        /// qaplay tc tabs --spreadsheet ADDRESS: 그 시트에 닿는지 보고, 탭마다 첫 줄의 칸 이름과 그 가운데 TC 번호, 내용, 기대 결과로 보이는 칸을 알려 준다.
        /// 닿지 못하면(공유하지 않음, 없는 시트, API를 켜지 않음) 까닭과 할 일을 알려 주고 종료 코드 1로 끝난다.
        /// </summary>
        public static int TabsCommand(Args args)
        {
            string spreadsheet = GoogleSheet.IdOf(args.Get("spreadsheet", null) ?? throw new UsageException("usage: qaplay tc tabs --spreadsheet ADDRESS"));
            GoogleKey key = Installed() ?? throw new UsageException("no Google key on this PC yet: run qaplay tc init, or use the setup window in Unity (qaplay > Google Sheet)");
            var api = new GoogleApi(key);
            var result = new Dictionary<string, object> { ["email"] = key.Email, ["address"] = GoogleSheet.Address(spreadsheet) };
            try
            {
                var tabs = new List<object>();
                foreach (Dictionary<string, object> tab in api.Tabs(spreadsheet))
                {
                    string title = (string)tab["title"];
                    List<List<string>> first = api.Values(spreadsheet, title, "1:1");
                    List<string> header = first.Count > 0 ? first[0] : new List<string>();
                    var columns = new Dictionary<string, object>();
                    foreach (string name in new[] { "tc", "content", "expected" }) columns[name] = Suggest(header, name);
                    tabs.Add(new Dictionary<string, object>
                    {
                        ["title"] = title, ["header"] = header, ["columns"] = columns, ["korean"] = header.Exists(Korean) || Korean(title),
                    });
                    Console.WriteLine(title + ": " + (header.Count == 0 ? "(empty)" : string.Join(", ", header)));
                }
                result["ok"] = true;
                result["tabs"] = tabs;
                Console.WriteLine(tabs.Count + " tab(s) in " + GoogleSheet.Address(spreadsheet));
                if (args.Json) Program.PrintJson(result);
                return 0;
            }
            catch (GoogleException e) when (e.Status == 403 || e.Status == 404)
            {
                string hint = GoogleSheet.Hint(e.Status, key.Email, spreadsheet);
                Console.WriteLine(e.Message);
                Console.WriteLine(hint);
                result["ok"] = false;
                result["status"] = e.Status;
                result["message"] = e.Message;
                result["hint"] = hint;
                if (args.Json) Program.PrintJson(result);
                return 1;
            }
        }

        // 키를 찾거나 받아서, 저장소 밖의 정해진 자리에 둔다
        static GoogleKey Key(Args args, Func<string, string> ask, Action<string> say)
        {
            string given = args.Get("credentials", null), fromEnv = Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS");
            if (given == null && !string.IsNullOrEmpty(fromEnv) && File.Exists(fromEnv))
            {
                say("using the Google key from GOOGLE_APPLICATION_CREDENTIALS: " + fromEnv);
                return GoogleKey.Read(fromEnv);
            }
            if (given == null && File.Exists(GoogleKey.DefaultFile))
            {
                say("using the Google key already on this PC: " + GoogleKey.DefaultFile);
                return GoogleKey.Read(GoogleKey.DefaultFile);
            }
            if (given != null) GoogleKey.Read(given); // 옵션으로 준 것이 틀렸으면 바로 알린다
            else say(KeySteps);
            while (given == null)
            {
                string answer = (ask("Path of the key file you downloaded (.json)") ?? throw Missing("--credentials FILE")).Trim().Trim('"');
                try
                {
                    if (!File.Exists(answer)) throw new UsageException("no such file: " + answer);
                    GoogleKey.Read(answer);
                    given = answer;
                }
                catch (UsageException e)
                {
                    say(e.Message);
                }
            }
            if (!Path.GetFullPath(given).Equals(Path.GetFullPath(GoogleKey.DefaultFile), StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(GoogleKey.DefaultFile));
                File.Copy(given, GoogleKey.DefaultFile, true);
                say("key saved to " + GoogleKey.DefaultFile + " (outside the project; the downloaded file is no longer needed)");
            }
            return GoogleKey.Read(GoogleKey.DefaultFile);
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

        static UsageException Missing(string option) => new UsageException("nobody is here to answer: give " + option);
    }
}
