using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Qaplay.Runner
{
    /// <summary>실행하는 동안 모아 둘 증거. Execute에 주면 실패한 화면을 찍고 게임의 로그를 남긴다.</summary>
    sealed class RunEvidence
    {
        /// <summary>러너가 띄운 게임의 로그를 저장해 둔 경로. 러너가 띄우지 않았으면 null.</summary>
        public string gameLog;
    }

    /// <summary>
    /// 실행 리포트 저장소. qaplay run 한 번이 폴더 하나다: run.json(요약, 규칙마다의 결과와 측정 수치)과
    /// 증거 파일(실패한 화면, 게임 로그, 메모리 기록). 시트에는 최신 결과만 있고 이력은 여기에 쌓인다.
    /// </summary>
    sealed class ReportStore
    {
        public const string DefaultFolder = "qaplay-reports";

        static readonly JsonSerializerOptions Pretty = new JsonSerializerOptions
        {
            WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        public readonly string Root;

        public ReportStore(string root)
        {
            Root = Path.GetFullPath(root);
        }

        public static ReportStore From(Args args) => new ReportStore(args.Get("reports", DefaultFolder));

        /// <summary>run이 결과를 남길 저장소. "--reports off"면 남기지 않는다(null).</summary>
        public static ReportStore ForRun(Args args) => args.Get("reports", DefaultFolder) == "off" ? null : From(args);

        public string FolderOf(string id) => Path.Combine(Root, id);

        /// <summary>모든 실행, 오래된 것부터. ID는 폴더 이름이다(run.json에 적힌 값은 믿지 않는다: 지울 폴더를 정하는 값이다).</summary>
        public List<Dictionary<string, object>> All()
        {
            var runs = new List<Dictionary<string, object>>();
            Unreadable.Clear();
            if (!Directory.Exists(Root)) return runs;
            string[] folders = Directory.GetDirectories(Root);
            Array.Sort(folders, StringComparer.Ordinal);
            foreach (string folder in folders)
            {
                string file = Path.Combine(folder, "run.json");
                if (!File.Exists(file)) continue;
                try
                {
                    var run = (Dictionary<string, object>)Json.Parse(File.ReadAllText(file));
                    TimeOf(run);
                    StatusOf(run);
                    if (!(run["rules"] is List<object> && run["evidence"] is List<object>)) throw new FormatException();
                    run["id"] = Path.GetFileName(folder);
                    runs.Add(run);
                }
                catch (Exception e) when (e is FormatException || e is InvalidCastException || e is KeyNotFoundException)
                {
                    // 쓰다가 멈췄거나 사람이 고치다 깨진 것. 이것 하나 때문에 실행 끝의 정리가 죽으면 안 된다. 건드리지 않고 넘어간다
                    Unreadable.Add(Path.GetFileName(folder));
                }
            }
            return runs;
        }

        /// <summary>마지막 All()에서 run.json을 읽지 못해 건너뛴 폴더들.</summary>
        public readonly List<string> Unreadable = new List<string>();

        /// <summary>ID로 실행 하나를 찾는다. "latest"는 가장 최근 것이다.</summary>
        public Dictionary<string, object> Load(string id)
        {
            List<Dictionary<string, object>> runs = All();
            Dictionary<string, object> run = id == "latest" ? (runs.Count > 0 ? runs[runs.Count - 1] : null) : runs.Find(r => id.Equals(r["id"]));
            return run ?? throw new UsageException("no run " + id + " in " + Root);
        }

        public void Save(Dictionary<string, object> run)
        {
            string folder = FolderOf((string)run["id"]);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "run.json"), JsonSerializer.Serialize(run, Pretty) + "\n", new UTF8Encoding(false));
        }

        /// <summary>이번 실행을 새 폴더에 남기고 그 ID를 돌려준다. 증거 파일은 폴더로 복사한다.</summary>
        public string Record(List<Outcome> outcomes, Dictionary<string, object> hello, string build, string device, double seconds,
            RunEvidence evidence, DateTime now)
        {
            // ID는 시작한 시각이다. 같은 초에 두 번 돌렸으면 뒤에 번호를 붙인다
            string stamp = now.ToLocalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture), id = stamp;
            for (int n = 2; Directory.Exists(FolderOf(id)); n++) id = stamp + "-" + n;
            string folder = FolderOf(id);
            Directory.CreateDirectory(folder);

            var rules = new List<object>();
            for (int n = 0; n < outcomes.Count; n++)
            {
                Outcome outcome = outcomes[n];
                // 폴더가 달라도 파일 이름이 같은 규칙이 있을 수 있어 순번을 붙인다
                string name = (n + 1).ToString("00", CultureInfo.InvariantCulture) + "-" + Path.GetFileNameWithoutExtension(outcome.file);
                var files = new List<object>();
                if (outcome.screenshot != null)
                {
                    File.WriteAllBytes(Path.Combine(folder, name + ".png"), outcome.screenshot);
                    files.Add(Item("screenshot", name + ".png"));
                }
                if (Copy(outcome.logPath, Path.Combine(folder, name + ".log"))) files.Add(Item("log", name + ".log"));
                if (Copy(outcome.memoryLog, Path.Combine(folder, name + ".memory.csv"))) files.Add(Item("data", name + ".memory.csv"));
                Dictionary<string, object> rule = outcome.Data();
                rule.Remove("memoryLog"); // 작업 폴더의 경로다. 리포트에는 복사본이 증거로 들어 있다
                rule["evidence"] = files;
                rules.Add(rule);
            }

            var runFiles = new List<object>();
            if (Copy(evidence?.gameLog, Path.Combine(folder, "game.log"))) runFiles.Add(Item("log", "game.log"));

            int passed = outcomes.FindAll(o => o.Status == "passed").Count, flaky = outcomes.FindAll(o => o.Status == "flaky").Count;
            var system = hello.GetValueOrDefault("device") as Dictionary<string, object>;
            Save(new Dictionary<string, object>
            {
                ["id"] = id,
                ["time"] = now.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                ["build"] = build, ["device"] = device,
                ["model"] = system?.GetValueOrDefault("model"), ["os"] = system?.GetValueOrDefault("os"),
                ["app"] = hello.GetValueOrDefault("app"), ["unity"] = hello.GetValueOrDefault("unity"),
                ["platform"] = hello.GetValueOrDefault("platform"), ["screen"] = hello.GetValueOrDefault("screen"),
                ["seconds"] = Math.Round(seconds, 1),
                ["passed"] = passed, ["flaky"] = flaky, ["failed"] = outcomes.Count - passed - flaky,
                ["keep"] = false,
                ["rules"] = rules,
                ["evidence"] = runFiles,
            });
            return id;
        }

        static Dictionary<string, object> Item(string kind, string file) => new Dictionary<string, object> { ["kind"] = kind, ["file"] = file };

        static bool Copy(string from, string to)
        {
            if (from == null || !File.Exists(from)) return false;
            File.Copy(from, to, true);
            return true;
        }

        /// <summary>실행 전체의 결과: 하나라도 실패했으면 failed, 아니고 불안정이 있으면 flaky.</summary>
        public static string StatusOf(Dictionary<string, object> run) =>
            Convert.ToInt32(run["failed"]) > 0 ? "failed" : Convert.ToInt32(run["flaky"]) > 0 ? "flaky" : "passed";

        public static DateTime TimeOf(Dictionary<string, object> run) =>
            DateTime.Parse((string)run["time"], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

        /// <summary>
        /// 기한이 지난 것을 찾는다. delete이면 지운다. 찾은 것을 "실행 ID/파일" 한 줄씩으로 돌려준다(실행 전체는 "실행 ID/").
        /// 증거는 종류와 결과에 따라 먼저 지워지고, 요약(run.json)은 증거가 하나도 남지 않은 뒤에 기한이 지나면 폴더째 지워진다.
        /// 중요 표시(keep)가 있는 실행은 건드리지 않는다.
        /// </summary>
        public List<string> Clean(Retention retention, DateTime now, bool delete)
        {
            var found = new List<string>();
            foreach (Dictionary<string, object> run in All())
            {
                if (true.Equals(run.GetValueOrDefault("keep"))) continue;
                string id = (string)run["id"], folder = FolderOf(id), build = run["build"] as string, device = run["device"] as string;
                double age = (now.ToUniversalTime() - TimeOf(run)).TotalDays;
                var tcs = new List<string>();
                bool left = false;

                void Check(object evidence, string result, List<string> ofTcs)
                {
                    foreach (object entry in (List<object>)evidence)
                    {
                        var item = (Dictionary<string, object>)entry;
                        // 파일 이름만 쓴다: run.json에 적힌 경로가 폴더 밖을 가리켜도 따라가지 않는다
                        string file = Path.GetFileName((string)item["file"]), path = Path.Combine(folder, file);
                        if (!File.Exists(path)) continue;
                        if (age <= retention.DaysFor((string)item["kind"], result, ofTcs, build, device))
                        {
                            left = true;
                            continue;
                        }
                        found.Add(id + "/" + file);
                        if (delete) File.Delete(path);
                    }
                }

                foreach (object entry in (List<object>)run["rules"])
                {
                    var rule = (Dictionary<string, object>)entry;
                    var own = new List<string>();
                    if (rule.GetValueOrDefault("tc") is string tc)
                    {
                        own.Add(tc);
                        tcs.Add(tc);
                    }
                    Check(rule["evidence"], (string)rule["status"], own);
                }
                Check(run["evidence"], StatusOf(run), tcs);

                if (left || age <= retention.DaysFor("summary", StatusOf(run), tcs, build, device)) continue;
                found.Add(id + "/");
                if (delete) Directory.Delete(folder, true);
            }
            return found;
        }
    }

    /// <summary>
    /// 얼마나 오래 둘지. 프로젝트 설정(qaplay.json)의 retention에 적는다(Unity의 Project Settings > qaplay). 값은 날 수이거나 "forever"다.
    /// 기본 기한 셋(요약, 통과한 규칙의 증거, 실패하거나 불안정한 규칙의 증거)과, 조건을 붙인 사용자 규칙이 있다.
    /// 여럿이 맞으면 가장 긴 기한을 쓴다.
    /// </summary>
    sealed class Retention
    {
        public static readonly string[] Names = { "summary", "passed", "failed" };
        static readonly string[] Conditions = { "result", "kind", "tc", "build", "device" };
        static readonly string[] Results = { "passed", "failed", "flaky" };
        static readonly string[] Kinds = { "summary", "screenshot", "log", "data" };

        readonly ProjectSettings m_Settings;
        readonly Dictionary<string, double> m_Days = new Dictionary<string, double> { ["summary"] = 365, ["passed"] = 14, ["failed"] = 90 };
        readonly List<object> m_Rules = new List<object>();

        Retention(ProjectSettings settings)
        {
            m_Settings = settings;
        }

        /// <summary>
        /// 설정의 기한. 예전처럼 리포트 폴더에 retention.json이 있으면 정리하지 않고 알린다:
        /// 그 파일의 값(더 긴 기한)을 모른 채 기본값으로 정리하면 남겨 두려던 증거가 지워진다.
        /// </summary>
        public static Retention From(ProjectSettings settings, ReportStore store)
        {
            string old = Path.Combine(store.Root, "retention.json");
            if (File.Exists(old))
                throw new UsageException(old + " is no longer read: the retention is set in Project Settings > qaplay now (\"retention\" in "
                                         + settings.File + "). Put its values there, then delete that file");
            var retention = new Retention(settings);
            if (settings.Retention != null) Parse(settings.Retention, settings.File + ": retention: ", retention);
            return retention;
        }

        /// <summary>설정의 retention 부분을 읽는다. 틀리면 UsageException. into가 null이면 확인만 한다.</summary>
        public static void Parse(Dictionary<string, object> data, string where, Retention into = null)
        {
            foreach (KeyValuePair<string, object> pair in data)
            {
                if (pair.Key == "rules")
                {
                    foreach (object entry in pair.Value as List<object> ?? throw new UsageException(where + "\"rules\" must be a list"))
                    {
                        // 오타 난 조건이 조용히 무시되면 "모든 것에 맞는 규칙"이 되어 아무것도 지워지지 않는다
                        var rule = entry as Dictionary<string, object> ?? throw new UsageException(where + "a rule must be an object");
                        var when = rule.GetValueOrDefault("when") as Dictionary<string, object> ?? throw new UsageException(where + "a rule needs \"when\"");
                        foreach (KeyValuePair<string, object> condition in when)
                        {
                            if (Array.IndexOf(Conditions, condition.Key) < 0)
                                throw new UsageException(where + "unknown condition \"" + condition.Key + "\" (use " + string.Join(", ", Conditions) + ")");
                            string[] allowed = condition.Key == "result" ? Results : condition.Key == "kind" ? Kinds : null;
                            if (!(condition.Value is string text) || allowed != null && Array.IndexOf(allowed, text) < 0)
                                throw new UsageException(where + "\"" + condition.Key + "\" must be " + (allowed == null ? "text" : "one of " + string.Join(", ", allowed)));
                        }
                        Days(rule.GetValueOrDefault("days"), where + "a rule's \"days\"");
                        into?.m_Rules.Add(rule);
                    }
                }
                else if (Array.IndexOf(Names, pair.Key) >= 0)
                {
                    double days = Days(pair.Value, where + "\"" + pair.Key + "\"");
                    if (into != null) into.m_Days[pair.Key] = days;
                }
                else throw new UsageException(where + "unknown name \"" + pair.Key + "\" (use " + string.Join(", ", Names) + ", rules)");
            }
        }

        /// <summary>날 수(0 이상의 수)나 "forever"를 읽는다.</summary>
        public static double Days(object value, string what)
        {
            if ("forever".Equals(value)) return double.PositiveInfinity;
            if (value is string text && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)) value = parsed;
            if (value is string || value == null || value is bool) throw new UsageException(what + " must be a number of days or \"forever\"");
            double days = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            if (days < 0 || double.IsNaN(days)) throw new UsageException(what + " must be a number of days or \"forever\"");
            return days;
        }

        public static object Show(double days) => double.IsPositiveInfinity(days) ? "forever" : (object)days;

        public double this[string name]
        {
            get => m_Days[name];
            set => m_Days[name] = value;
        }

        public List<object> Rules => m_Rules;

        /// <summary>설정 파일의 retention에 적는다. 설정의 다른 부분은 그대로다.</summary>
        public void Save()
        {
            var data = new Dictionary<string, object>();
            foreach (string name in Names) data[name] = Show(m_Days[name]);
            if (m_Rules.Count > 0) data["rules"] = m_Rules;
            m_Settings.Set("retention", data);
            m_Settings.Save();
        }

        /// <summary>이 항목을 며칠 두는지. kind는 summary나 증거의 종류, result는 그 규칙(요약이면 실행 전체)의 결과다.</summary>
        public double DaysFor(string kind, string result, List<string> tcs, string build, string device)
        {
            double days = m_Days[kind == "summary" ? "summary" : result == "passed" ? "passed" : "failed"];
            foreach (object entry in m_Rules)
            {
                var rule = (Dictionary<string, object>)entry;
                var when = (Dictionary<string, object>)rule["when"];
                bool Is(string name, string actual) => !when.TryGetValue(name, out object wanted) || Like((string)wanted, actual);
                bool tc = !when.TryGetValue("tc", out object pattern) || tcs.Exists(id => Like((string)pattern, id));
                if (Is("kind", kind) && Is("result", result) && tc && Is("build", build) && Is("device", device))
                    days = Math.Max(days, Days(rule["days"], "days"));
            }
            return days;
        }

        // *는 아무 글자나 몇 개든. 대소문자는 가리지 않는다
        static bool Like(string pattern, string actual) =>
            actual != null && Regex.IsMatch(actual, "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>qaplay reports ...: 실행 이력을 보고, 중요 표시를 하고, 기한이 지난 것을 정리한다.</summary>
    static class ReportCommands
    {
        const string Usage =
            "usage:\n" +
            "  qaplay reports list                  the runs kept here, newest first\n" +
            "  qaplay reports show ID               one run: every rule, its failures, numbers and evidence files (ID can be \"latest\")\n" +
            "  qaplay reports keep ID               mark a run as important: it is never cleaned up\n" +
            "  qaplay reports unkeep ID\n" +
            "  qaplay reports delete ID\n" +
            "  qaplay reports expired               what clean would remove now, without removing anything\n" +
            "  qaplay reports clean                 remove the evidence and the runs past their retention (run does this too)\n" +
            "  qaplay reports retention [summary=DAYS] [passed=DAYS] [failed=DAYS]\n" +
            "                                       show, or change, how long things are kept (DAYS: a number or \"forever\")\n" +
            "  [--reports DIR]                      where runs are kept (default qaplay-reports; with run, \"off\" keeps nothing)\n" +
            "  [--json]                             list, show, expired, retention: print the result as JSON on stdout";

        public static int Run(Args args, ProjectSettings settings)
        {
            ReportStore store = ReportStore.From(args);
            string action = args.Paths.Count > 0 ? args.Paths[0] : "";
            string Id() => args.Paths.Count == 2 ? args.Paths[1] : throw new UsageException("usage: qaplay reports " + action + " ID");
            switch (action)
            {
                case "list":
                {
                    List<Dictionary<string, object>> runs = store.All();
                    runs.Reverse();
                    foreach (Dictionary<string, object> run in runs) Console.WriteLine(Line(run));
                    Console.WriteLine(runs.Count + " run(s) in " + store.Root);
                    if (store.Unreadable.Count > 0)
                        Console.WriteLine(store.Unreadable.Count + " folder(s) with a run.json that cannot be read, left alone: " + string.Join(", ", store.Unreadable));
                    if (args.Json) Program.PrintJson(new Dictionary<string, object> { ["folder"] = store.Root, ["runs"] = runs.ConvertAll(Brief) });
                    return 0;
                }
                case "show":
                {
                    Dictionary<string, object> run = store.Load(Id());
                    string folder = store.FolderOf((string)run["id"]);
                    Console.WriteLine(Line(run));
                    foreach (string name in new[] { "model", "os", "unity" })
                        if (run.GetValueOrDefault(name) != null) Console.WriteLine(name + ": " + run[name]);
                    foreach (object entry in (List<object>)run["rules"])
                    {
                        var rule = (Dictionary<string, object>)entry;
                        string status = (string)rule["status"];
                        Console.WriteLine((status == "passed" ? "PASS  " : status == "flaky" ? "FLAKY " : "FAIL  ") + rule["file"]
                                          + (rule.GetValueOrDefault("tc") == null ? "" : "  " + rule["tc"])
                                          + (rule.GetValueOrDefault("bug") == null ? "" : "  BUG-" + rule["bug"]));
                        var failures = (List<object>)rule["failures"];
                        for (int n = 0; n < failures.Count; n++) Console.WriteLine("      attempt " + (n + 1) + ": " + failures[n]);
                        if (rule.GetValueOrDefault("frames") is Dictionary<string, object> frames) Console.WriteLine("      frames: " + Outcome.FrameSummary(frames));
                        if (rule.GetValueOrDefault("device") is Dictionary<string, object> device) Console.WriteLine("      device: " + DeviceMeter.Summary(device));
                        Files(rule["evidence"], folder, "      ");
                    }
                    Files(run["evidence"], folder, "");
                    Console.WriteLine("folder: " + folder);
                    if (args.Json)
                    {
                        run["folder"] = folder;
                        Program.PrintJson(run);
                    }
                    return 0;
                }
                case "keep":
                case "unkeep":
                {
                    Dictionary<string, object> run = store.Load(Id());
                    run["keep"] = action == "keep";
                    store.Save(run);
                    Console.WriteLine(action == "keep" ? "run " + run["id"] + " is kept: it will not be cleaned up" : "run " + run["id"] + " follows the retention again");
                    return 0;
                }
                case "delete":
                {
                    Dictionary<string, object> run = store.Load(Id());
                    Directory.Delete(store.FolderOf((string)run["id"]), true);
                    Console.WriteLine("run " + run["id"] + " deleted");
                    return 0;
                }
                case "expired":
                case "clean":
                {
                    if (args.Paths.Count != 1) throw new UsageException("usage: qaplay reports " + action);
                    List<string> found = store.Clean(Retention.From(settings, store), DateTime.UtcNow, action == "clean");
                    foreach (string item in found) Console.WriteLine(item);
                    Console.WriteLine(Counts(found) + (action == "clean" ? " removed" : " past their retention (remove them with: qaplay reports clean)"));
                    if (args.Json) Program.PrintJson(new Dictionary<string, object> { ["expired"] = found });
                    return 0;
                }
                case "retention":
                {
                    Retention retention = Retention.From(settings, store);
                    for (int n = 1; n < args.Paths.Count; n++)
                    {
                        string[] pair = args.Paths[n].Split('=');
                        if (pair.Length != 2 || Array.IndexOf(Retention.Names, pair[0]) < 0)
                            throw new UsageException("usage: qaplay reports retention [summary=DAYS] [passed=DAYS] [failed=DAYS]");
                        retention[pair[0]] = Retention.Days(pair[1], pair[0]);
                    }
                    if (args.Paths.Count > 1) retention.Save();
                    var data = new Dictionary<string, object>();
                    foreach (string name in Retention.Names)
                    {
                        data[name] = Retention.Show(retention[name]);
                        Console.WriteLine(name + ": " + (double.IsPositiveInfinity(retention[name]) ? "forever" : retention[name].ToString(CultureInfo.InvariantCulture) + " day(s)"));
                    }
                    Console.WriteLine(retention.Rules.Count + " custom rule(s); the retention is in " + settings.File + " (Project Settings > qaplay)");
                    data["rules"] = retention.Rules;
                    if (args.Json) Program.PrintJson(data);
                    return 0;
                }
                default:
                    Console.Error.WriteLine(Usage);
                    return 2;
            }
        }

        /// <summary>Clean이 돌려준 줄을 "증거 파일 몇 개, 실행 몇 개"로 센다.</summary>
        public static string Counts(List<string> found)
        {
            int runs = found.FindAll(item => item.EndsWith("/", StringComparison.Ordinal)).Count;
            return (found.Count - runs) + " evidence file(s) and " + runs + " run(s)";
        }

        static string Line(Dictionary<string, object> run)
        {
            int passed = Convert.ToInt32(run["passed"]), flaky = Convert.ToInt32(run["flaky"]), failed = Convert.ToInt32(run["failed"]);
            return run["id"] + "  " + ReportStore.TimeOf(run).ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
                   + "  " + run["build"] + " / " + run["device"] + "  " + (passed + flaky + failed) + " rule(s): " + passed + " passed, " + flaky + " flaky, "
                   + failed + " failed" + (true.Equals(run.GetValueOrDefault("keep")) ? "  [kept]" : "");
        }

        /// <summary>목록에 나가는 모습: 규칙마다의 내용을 뺀 요약.</summary>
        public static object Brief(Dictionary<string, object> run)
        {
            var brief = new Dictionary<string, object>();
            foreach (string name in new[] { "id", "time", "build", "device", "seconds", "passed", "flaky", "failed", "keep" }) brief[name] = run.GetValueOrDefault(name);
            return brief;
        }

        static void Files(object evidence, string folder, string indent)
        {
            foreach (object entry in (List<object>)evidence)
            {
                var item = (Dictionary<string, object>)entry;
                string path = Path.Combine(folder, Path.GetFileName((string)item["file"]));
                Console.WriteLine(indent + item["kind"] + ": " + (File.Exists(path) ? path : item["file"] + " (removed: past its retention)"));
            }
        }
    }
}
