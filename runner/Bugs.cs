using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Qaplay.Runner
{
    /// <summary>
    /// 버그 리포트 저장소. 폴더 하나(기본 qaplay-bugs) 아래에 버그마다 하위 폴더가 하나씩 있다:
    /// bug.json(제목, 상태 등), steps.yaml(재현 스텝), log.txt, screenshot.png.
    /// </summary>
    // ponytail: 파일이 곧 저장소다. 동시에 두 러너가 같은 폴더에 쓰는 경우는 막지 않는다.
    // 수정 이력, 합치기와 나누기, 이슈 관리 도구 연동은 없다(9단계).
    sealed class BugStore
    {
        public const string DefaultFolder = "qaplay-bugs";
        public static readonly string[] Statuses = { "open", "resolved", "verified", "closed" };
        public static readonly string[] Severities = { "critical", "major", "normal", "minor" };

        static readonly JsonSerializerOptions Pretty = new JsonSerializerOptions
        {
            WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        readonly string m_Root;

        public BugStore(string root)
        {
            m_Root = root;
        }

        public static BugStore From(Args args) => new BugStore(args.Get("bugs", DefaultFolder));

        /// <summary>run이 예외, 크래시, 프리징을 올릴 저장소. "--bugs off"면 올리지 않는다(null).</summary>
        public static BugStore ForAutoReports(Args args) => args.Get("bugs", DefaultFolder) == "off" ? null : From(args);

        public string FolderOf(string id) => Path.Combine(m_Root, id);

        /// <summary>사람이 적은 번호를 저장소의 ID로 바꾼다: "3", "0003", "BUG-3"은 모두 "0003".</summary>
        public static string NormalizeId(string text)
        {
            string digits = text.StartsWith("BUG-", StringComparison.OrdinalIgnoreCase) ? text.Substring(4) : text;
            if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out int number) || number <= 0)
                throw new UsageException("not a bug id: " + text);
            return number.ToString("0000", CultureInfo.InvariantCulture);
        }

        public List<Dictionary<string, object>> All()
        {
            var bugs = new List<Dictionary<string, object>>();
            if (!Directory.Exists(m_Root)) return bugs;
            string[] folders = Directory.GetDirectories(m_Root);
            Array.Sort(folders, StringComparer.Ordinal);
            foreach (string folder in folders)
            {
                string file = Path.Combine(folder, "bug.json");
                if (File.Exists(file)) bugs.Add((Dictionary<string, object>)Json.Parse(File.ReadAllText(file)));
            }
            return bugs;
        }

        public Dictionary<string, object> Load(string id)
        {
            string file = Path.Combine(FolderOf(id), "bug.json");
            if (!File.Exists(file)) throw new UsageException("no bug " + id + " in " + m_Root);
            return (Dictionary<string, object>)Json.Parse(File.ReadAllText(file));
        }

        public void Save(Dictionary<string, object> bug)
        {
            string folder = FolderOf((string)bug["id"]);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "bug.json"), JsonSerializer.Serialize(bug, Pretty) + "\n", new UTF8Encoding(false));
        }

        /// <summary>새 버그를 만들어 저장한다. 번호는 지금까지의 가장 큰 번호 다음이다.</summary>
        public Dictionary<string, object> Create(string kind, string title)
        {
            int last = 0;
            foreach (Dictionary<string, object> existing in All())
                if (int.TryParse(existing["id"] as string, out int number)) last = Math.Max(last, number);
            string now = Now();
            var bug = new Dictionary<string, object>
            {
                ["id"] = (last + 1).ToString("0000", CultureInfo.InvariantCulture),
                ["title"] = title,
                ["description"] = "",
                ["status"] = "open",
                // 심각도는 실패 종류로 초안을 채운다. 우선순위는 사람이 정하도록 비워 둔다
                ["severity"] = kind == "crash" || kind == "freeze" ? "critical" : kind == "exception" ? "major" : "",
                ["priority"] = "",
                ["kind"] = kind,
                ["count"] = 1,
                ["firstSeen"] = now,
                ["lastSeen"] = now,
                ["regression"] = false,
            };
            Save(bug);
            return bug;
        }

        public static string Now() => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

        /// <summary>다시 연다. 고쳤다고 확인했거나 닫았던 버그가 다시 난 것이면 회귀로 표시한다.</summary>
        public static void Reopen(Dictionary<string, object> bug)
        {
            if ("verified".Equals(bug["status"]) || "closed".Equals(bug["status"])) bug["regression"] = true;
            bug["status"] = "open";
        }

        /// <summary>
        /// 버그임이 분명한 실패(예외, 크래시, 프리징)를 버그로 올린다. 같은 버그가 이미 있으면 새로 만들지 않고
        /// 횟수를 올린다. 한 일을 한 줄로 돌려준다. 올릴 것이 아니면 null.
        /// </summary>
        public string RecordAuto(Outcome outcome, Dictionary<string, object> info)
        {
            string kind = outcome.failureCode;
            if (outcome.passed || !(kind == "exception" || kind == "crash" || kind == "freeze")) return null;

            string ruleName = Path.GetFileNameWithoutExtension(outcome.file);
            string fingerprint = Fingerprint(kind, ruleName, outcome.evidence);
            Dictionary<string, object> bug = All().Find(b => fingerprint.Equals(b.GetValueOrDefault("fingerprint")));
            string note;
            if (bug == null)
            {
                string title = kind == "exception"
                    ? outcome.evidence.Split('\n')[0].Trim()
                    : (kind == "crash" ? "Crash" : "Freeze") + " while running " + ruleName;
                bug = Create(kind, title);
                bug["fingerprint"] = fingerprint;
                bug["description"] = outcome.failures[outcome.failures.Count - 1];
                if (info != null)
                    foreach (string key in new[] { "app", "unity", "platform", "screen" })
                        if (info.TryGetValue(key, out object value)) bug[key] = value;
                note = "bug " + bug["id"] + " registered";
            }
            else
            {
                bug["count"] = Convert.ToInt32(bug["count"]) + 1;
                bug["lastSeen"] = Now();
                bool wasOpen = "open".Equals(bug["status"]);
                Reopen(bug);
                note = "bug " + bug["id"] + " seen " + bug["count"] + " times"
                       + (wasOpen ? "" : true.Equals(bug["regression"]) ? ", reopened as a regression" : ", reopened");
            }
            Save(bug);
            outcome.bugId = (string)bug["id"];

            // 재현 스텝은 그 규칙 파일이다. 로그는 가장 최근 것을 남긴다
            string folder = FolderOf((string)bug["id"]);
            File.Copy(outcome.file, Path.Combine(folder, "steps.yaml"), true);
            if (outcome.logPath != null && File.Exists(outcome.logPath)) File.Copy(outcome.logPath, Path.Combine(folder, "log.txt"), true);
            else if (outcome.evidence != null) File.WriteAllText(Path.Combine(folder, "log.txt"), outcome.evidence + "\n", new UTF8Encoding(false));
            return note;
        }

        /// <summary>
        /// 같은 버그인지 가리는 값. 예외는 종류와 맨 위 호출 지점이고, 크래시와 프리징은 어느 규칙에서 났는지다.
        /// </summary>
        // ponytail: 크래시의 네이티브 스택은 읽지 않는다. 같은 규칙에서 난 크래시는 원인이 달라도 한 버그로 묶인다.
        // 심볼리케이션을 넣을 때 스택 기준으로 바꾼다.
        public static string Fingerprint(string kind, string ruleName, string evidence)
        {
            if (kind != "exception" || evidence == null) return kind + "|" + ruleName;
            string[] lines = evidence.Split('\n');
            string type = lines[0].Split(':')[0].Trim();
            string frame = lines.Length > 1 ? lines[1] : "";
            int at = frame.IndexOf(" (at ", StringComparison.Ordinal); // 빌드마다 달라지는 파일 위치는 뺀다
            if (at >= 0) frame = frame.Substring(0, at);
            return "exception|" + type + "|" + frame.Trim();
        }
    }

    /// <summary>qaplay bugs ...: 버그 리포트를 가져오고, 보고, 고치고, 회귀 규칙으로 만든다.</summary>
    static class BugCommands
    {
        const string Usage =
            "usage:\n" +
            "  qaplay bugs pull                     bring the reports saved in the game (F12 or a three-finger hold) to this PC\n" +
            "  qaplay bugs report [--title T] [--description D]\n" +
            "                                       take a report of the game right now and bring it here\n" +
            "  qaplay bugs list [--status S]        open | resolved | verified | closed\n" +
            "  qaplay bugs show ID\n" +
            "  qaplay bugs set ID [--title T] [--description D] [--status S] [--severity S] [--priority P]\n" +
            "  qaplay bugs delete ID\n" +
            "  qaplay bugs promote ID [--to DIR]    turn the steps of a bug into a regression rule (default folder: regression)\n" +
            "  qaplay bugs verify ID                run that rule: passing marks the bug verified, failing reopens it\n" +
            "  [--bugs DIR]                         where bugs are kept (default qaplay-bugs)";

        public static int Run(Args args)
        {
            var store = BugStore.From(args);
            switch (args.Paths.Count > 0 ? args.Paths[0] : "")
            {
                case "pull": return Pull(args, store, false);
                case "report": return Pull(args, store, true);
                case "list": return List(args, store);
                case "show": return Show(store, Id(args));
                case "set": return Set(args, store, Id(args));
                case "delete": return Delete(store, Id(args));
                case "promote": return Promote(args, store, Id(args));
                case "verify": return Verify(args, store, Id(args));
                default: throw new UsageException(Usage);
            }
        }

        static string Id(Args args) =>
            args.Paths.Count > 1 ? BugStore.NormalizeId(args.Paths[1]) : throw new UsageException("this command needs a bug id");

        static Dictionary<string, object> Ok(Dictionary<string, object> response)
        {
            if (true.Equals(response["ok"])) return (Dictionary<string, object>)response["result"];
            var error = (Dictionary<string, object>)response["error"];
            throw new IOException("the game answered " + error["code"] + ": " + error["message"]);
        }

        static Dictionary<string, object> ReportId(string id) => new Dictionary<string, object> { ["id"] = id };

        // 게임 안에 쌓인 제보를 버그로 가져온다. takeOneFirst면 그 전에 지금 화면으로 제보를 하나 만든다
        static int Pull(Args args, BugStore store, bool takeOneFirst)
        {
            using AgentClient client = Program.Connect(args);
            if (takeOneFirst)
            {
                var report = new Dictionary<string, object>();
                foreach (string field in new[] { "title", "description" })
                    if (args.Get(field, null) != null) report[field] = args.Get(field, null);
                Ok(client.Request("report", report));
            }

            int pulled = 0;
            foreach (object summary in (List<object>)Ok(client.Request("reports", null))["reports"])
            {
                string reportId = (string)((Dictionary<string, object>)summary)["id"];
                // 저장까지 하고 기기에서 지우기 전에 끊겼던 제보는 다시 만들지 않는다
                if (!store.All().Exists(b => reportId.Equals(b.GetValueOrDefault("reportId"))))
                {
                    Dictionary<string, object> content = Ok(client.Request("reportGet", ReportId(reportId)));
                    var report = (Dictionary<string, object>)content["report"];
                    Dictionary<string, object> bug = store.Create("manual", (string)report["title"]);
                    bug["description"] = report["description"];
                    bug["reportId"] = reportId;
                    bug["firstSeen"] = bug["lastSeen"] = report["createdAt"];
                    foreach (string key in new[] { "app", "unity", "platform", "device", "screen" })
                        if (report.TryGetValue(key, out object value)) bug[key] = value;
                    store.Save(bug);

                    string folder = store.FolderOf((string)bug["id"]);
                    var utf8 = new UTF8Encoding(false);
                    File.WriteAllText(Path.Combine(folder, "steps.yaml"), (string)content["steps"], utf8);
                    File.WriteAllText(Path.Combine(folder, "log.txt"), (string)content["log"], utf8);
                    if (content.TryGetValue("screenshot", out object png))
                        File.WriteAllBytes(Path.Combine(folder, "screenshot.png"), Convert.FromBase64String((string)png));
                    Console.WriteLine(bug["id"] + "  " + bug["title"]);
                    pulled++;
                }
                Ok(client.Request("reportDelete", ReportId(reportId)));
            }
            if (pulled == 0) Console.WriteLine("no new reports");
            return 0;
        }

        static int List(Args args, BugStore store)
        {
            string status = args.Get("status", null);
            List<Dictionary<string, object>> bugs = store.All().FindAll(b => status == null || status.Equals(b["status"]));
            if (bugs.Count == 0) Console.WriteLine("no bugs");
            foreach (Dictionary<string, object> bug in bugs)
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0}  {1,-8}  {2,-8}  {3,-9}  x{4}  {5}{6}",
                    bug["id"], bug["status"], bug["severity"], bug["kind"], bug["count"], bug["title"],
                    true.Equals(bug["regression"]) ? "  [regression]" : ""));
            return 0;
        }

        static int Show(BugStore store, string id)
        {
            Dictionary<string, object> bug = store.Load(id);
            foreach (KeyValuePair<string, object> field in bug)
                Console.WriteLine(field.Key + ": " + (field.Value is string || field.Value == null ? field.Value : Json.Write(field.Value)));
            string folder = store.FolderOf(id);
            foreach (string name in new[] { "screenshot.png", "log.txt", "steps.yaml" })
                if (File.Exists(Path.Combine(folder, name))) Console.WriteLine(name + ": " + Path.GetFullPath(Path.Combine(folder, name)));
            string steps = Path.Combine(folder, "steps.yaml");
            if (File.Exists(steps)) Console.WriteLine("--- steps\n" + File.ReadAllText(steps).TrimEnd());
            return 0;
        }

        static int Set(Args args, BugStore store, string id)
        {
            Dictionary<string, object> bug = store.Load(id);
            bool changed = false;
            foreach (string field in new[] { "title", "description", "status", "severity", "priority" })
            {
                string value = args.Get(field, null);
                if (value == null) continue;
                if (field == "status" && Array.IndexOf(BugStore.Statuses, value) < 0)
                    throw new UsageException("--status must be one of: " + string.Join(", ", BugStore.Statuses));
                if (field == "severity" && Array.IndexOf(BugStore.Severities, value) < 0)
                    throw new UsageException("--severity must be one of: " + string.Join(", ", BugStore.Severities));
                bug[field] = value;
                changed = true;
            }
            if (!changed) throw new UsageException("nothing to change; give --title, --description, --status, --severity or --priority");
            store.Save(bug);
            Console.WriteLine("bug " + id + " updated");
            return 0;
        }

        static int Delete(BugStore store, string id)
        {
            store.Load(id);
            Directory.Delete(store.FolderOf(id), true);
            Console.WriteLine("bug " + id + " deleted");
            return 0;
        }

        // 버그의 재현 스텝을 회귀 규칙 파일로 만든다. 통과 조건(확인 명령)은 사람이 덧붙인다
        static int Promote(Args args, BugStore store, string id)
        {
            Dictionary<string, object> bug = store.Load(id);
            string stepsFile = Path.Combine(store.FolderOf(id), "steps.yaml");
            string steps = File.Exists(stepsFile) ? File.ReadAllText(stepsFile).Replace("\r\n", "\n").TrimEnd() + "\n" : "";
            try
            {
                RuleReader.Read(steps);
            }
            catch (FormatException e)
            {
                throw new UsageException("bug " + id + " has no steps that can be replayed: " + e.Message);
            }

            string path = Path.Combine(args.Get("to", "regression"), "bug-" + id + ".yaml");
            if (File.Exists(path)) throw new UsageException(path + " already exists");
            string title = ((string)bug["title"]).Replace("\r", " ").Replace("\n", " ");
            var rule = new StringBuilder();
            rule.Append("# regression rule for bug ").Append(id).Append(": ").Append(title).Append('\n');
            rule.Append("# add the checks that tell fixed from still broken after the steps, e.g. - assertVisible: { name: ... }\n");
            // 자동 등록된 버그의 스텝은 머리말까지 갖춘 규칙 파일이다. 그대로 쓴다
            bool wholeRule = Array.Exists(steps.Split('\n'), line => line.TrimEnd() == "---");
            if (!wholeRule) rule.Append("name: ").Append(Json.Write("regression: " + title)).Append("\n---\n");
            rule.Append(steps);

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, rule.ToString(), new UTF8Encoding(false));
            bug["rule"] = path;
            store.Save(bug);
            Console.WriteLine(path);
            return 0;
        }

        // 회귀 규칙을 돌려 고쳐졌는지 확인한다
        static int Verify(Args args, BugStore store, string id)
        {
            Dictionary<string, object> bug = store.Load(id);
            if (!(bug.GetValueOrDefault("rule") is string rule) || !File.Exists(rule))
                throw new UsageException("bug " + id + " has no regression rule yet; run: qaplay bugs promote " + id);

            List<Outcome> outcomes = Program.Execute(args, new List<string> { rule }, out _);
            bug = store.Load(id); // 실행 중에 같은 버그가 다시 잡혀 갱신됐을 수 있다
            bool passed = outcomes[0].passed;
            if (passed) bug["status"] = "verified";
            else BugStore.Reopen(bug);
            store.Save(bug);
            Console.WriteLine("bug " + id + (passed ? " verified: the regression rule passes" : " is open: the regression rule fails"));
            return passed ? 0 : 1;
        }
    }
}
