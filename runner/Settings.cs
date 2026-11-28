using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Qaplay.Runner
{
    /// <summary>
    /// 프로젝트 설정 파일(qaplay.json). Unity 프로젝트의 맨 위 폴더에 두고 저장소에 넣는다. Unity의 설정 화면(Project Settings > qaplay)이 쓰고,
    /// 러너는 실행한 폴더의 것을 읽는다(--settings FILE로 다른 것을 고른다). 값을 정하는 순서는 명령줄, 이 파일, 기본값이다.
    /// 안의 경로는 이 파일이 있는 폴더 기준이다.
    /// </summary>
    sealed class ProjectSettings
    {
        public const string DefaultFile = "qaplay.json";

        static readonly string[] TopKeys = { "target", "rules", "run", "reports", "retention", "dashboard", "bugs", "bugReport", "artifacts", "exceptions", "sheet" };
        static readonly string[] TargetKeys = { "kind", "app", "appArgs", "device", "package" };
        static readonly string[] Kinds = { "editor", "pc", "android" };
        // run 아래의 값과, 그것이 대신하는 명령줄 옵션
        static readonly Dictionary<string, string> RunKeys = new Dictionary<string, string>
        {
            ["retries"] = "retries", ["freezeSeconds"] = "freeze-seconds", ["launchSeconds"] = "launch-seconds", ["port"] = "port",
        };
        // 맨 위의 경로 값과, 그것이 대신하는 명령줄 옵션
        static readonly Dictionary<string, string> PathKeys = new Dictionary<string, string>
        {
            ["reports"] = "reports", ["bugs"] = "bugs", ["artifacts"] = "artifacts", ["exceptions"] = "exceptions",
        };

        /// <summary>이 설정의 파일. 아직 없을 수 있다(그때 Save가 만든다).</summary>
        public readonly string File;
        public readonly string Folder;
        /// <summary>파일의 내용 전부. 이 러너가 모르는 일이 없도록 읽을 때 모든 키를 확인한다.</summary>
        readonly Dictionary<string, object> m_Json;

        ProjectSettings(string file, Dictionary<string, object> json)
        {
            File = Path.GetFullPath(file);
            Folder = Path.GetDirectoryName(File);
            m_Json = json;
        }

        public bool Exists => System.IO.File.Exists(File);

        /// <summary>--settings로 준 파일, 없으면 실행한 폴더의 qaplay.json. 파일이 없으면 빈 설정이다.</summary>
        public static ProjectSettings Load(Args args) => Load(args.Get("settings", DefaultFile));

        public static ProjectSettings Load(string file)
        {
            if (!System.IO.File.Exists(file)) return new ProjectSettings(file, new Dictionary<string, object>());
            try
            {
                var json = Qaplay.Json.Parse(System.IO.File.ReadAllText(file)) as Dictionary<string, object>
                           ?? throw new FormatException("it is not a JSON object");
                var settings = new ProjectSettings(file, json);
                settings.Validate();
                return settings;
            }
            catch (Exception e) when (e is FormatException || e is InvalidCastException)
            {
                throw new UsageException("cannot read " + Path.GetFullPath(file) + ": " + e.Message);
            }
        }

        // 모르는 키와 틀린 값을 읽을 때 알린다. 오타 난 설정이 조용히 무시되면 사람은 그 값이 쓰인 줄 안다
        void Validate()
        {
            Known(m_Json, TopKeys, "");
            if (m_Json.TryGetValue("target", out object target))
            {
                var map = target as Dictionary<string, object> ?? throw new FormatException("\"target\" must be an object");
                Known(map, TargetKeys, "target.");
                foreach (string key in TargetKeys)
                    if (map.TryGetValue(key, out object value) && !(value is string))
                        throw new FormatException("target." + key + " must be text");
                if (Kind != null && Array.IndexOf(Kinds, Kind) < 0) throw new FormatException("target.kind must be editor, pc or android");
            }
            if (m_Json.TryGetValue("run", out object run))
            {
                var map = run as Dictionary<string, object> ?? throw new FormatException("\"run\" must be an object");
                Known(map, new List<string>(RunKeys.Keys).ToArray(), "run.");
                foreach (KeyValuePair<string, object> pair in map)
                    if (!(pair.Value is double number) || number < 0 || number != Math.Floor(number) || number > int.MaxValue)
                        throw new FormatException("run." + pair.Key + " must be a whole number, 0 or greater");
            }
            foreach (string key in PathKeys.Keys)
                if (m_Json.TryGetValue(key, out object value) && !(value is string))
                    throw new FormatException("\"" + key + "\" must be text (a path)");
            if (m_Json.TryGetValue("rules", out object rules))
                foreach (object folder in rules as List<object> ?? throw new FormatException("\"rules\" must be a list of folders"))
                    if (!(folder is string)) throw new FormatException("\"rules\" must be a list of folders");
            if (m_Json.TryGetValue("sheet", out object sheet) && !(sheet is Dictionary<string, object>))
                throw new FormatException("\"sheet\" must be an object");
            // 게임 안의 에이전트가 읽는 값(테스터가 버그를 제보하는 방법). 러너는 틀린 것만 알린다. 키 이름은 에이전트가 확인한다
            if (m_Json.TryGetValue("bugReport", out object bugReport))
            {
                var map = bugReport as Dictionary<string, object> ?? throw new FormatException("\"bugReport\" must be an object");
                Known(map, new[] { "key", "fingers", "seconds" }, "bugReport.");
                if (map.TryGetValue("key", out object key) && !(key is string)) throw new FormatException("bugReport.key must be the name of a key (F12) or none");
                if (map.TryGetValue("fingers", out object fingers) && (!(fingers is double count) || count != Math.Floor(count) || count < 0 || count == 1 || count > 10))
                    throw new FormatException("bugReport.fingers must be 0 (off) or 2 to 10");
                if (map.TryGetValue("seconds", out object seconds) && (!(seconds is double time) || time < 0.2 || time > 10))
                    throw new FormatException("bugReport.seconds must be from 0.2 to 10");
            }
            if (m_Json.TryGetValue("dashboard", out object dashboard))
                DashboardView.Parse(dashboard as Dictionary<string, object> ?? throw new FormatException("\"dashboard\" must be an object"));
            if (m_Json.TryGetValue("retention", out object retention))
                try
                {
                    Runner.Retention.Parse(retention as Dictionary<string, object> ?? throw new FormatException("\"retention\" must be an object"), "retention: ");
                }
                catch (UsageException e)
                {
                    throw new FormatException(e.Message);
                }
        }

        static void Known(Dictionary<string, object> map, string[] keys, string where)
        {
            foreach (string key in map.Keys)
                if (Array.IndexOf(keys, key) < 0)
                    throw new FormatException("unknown name \"" + where + key + "\" (use " + string.Join(", ", keys) + ")");
        }

        Dictionary<string, object> Target => m_Json.GetValueOrDefault("target") as Dictionary<string, object>;

        string TargetValue(string key) => Target?.GetValueOrDefault(key) is string text && text.Trim().Length > 0 ? text.Trim() : null;

        /// <summary>게임을 어디서 돌리는지: editor(에디터의 Play), pc(PC 빌드), android. 적지 않았으면 null(에디터와 같다).</summary>
        public string Kind => TargetValue("kind");

        /// <summary>규칙 파일이 있는 폴더들(전체 경로). run에 규칙을 주지 않으면 이것을 돌리고, 시트의 규칙 상태도 여기서 찾는다.</summary>
        public List<string> Rules
        {
            get
            {
                var folders = new List<string>();
                if (m_Json.GetValueOrDefault("rules") is List<object> rules)
                    foreach (object folder in rules)
                        if (((string)folder).Trim().Length > 0) folders.Add(PathOf((string)folder));
                return folders;
            }
        }

        /// <summary>규칙 폴더들을 파일에 적힌 모습 그대로(이 파일의 폴더 기준).</summary>
        public List<string> RulesAsWritten
        {
            get
            {
                var folders = new List<string>();
                if (m_Json.GetValueOrDefault("rules") is List<object> rules)
                    foreach (object folder in rules) folders.Add((string)folder);
                return folders;
            }
        }

        /// <summary>리포트의 보관 기한(Retention이 읽는다). 적지 않았으면 null(기본 기한).</summary>
        public Dictionary<string, object> Retention => m_Json.GetValueOrDefault("retention") as Dictionary<string, object>;

        /// <summary>대시보드의 설정(DashboardView가 읽는다). 적지 않았으면 null.</summary>
        public Dictionary<string, object> Dashboard => m_Json.GetValueOrDefault("dashboard") as Dictionary<string, object>;

        /// <summary>시트 설정. 이어진 시트가 없으면 null.</summary>
        public Dictionary<string, object> Sheet => m_Json.GetValueOrDefault("sheet") as Dictionary<string, object>;

        public string PathOf(string relative) => Path.GetFullPath(Path.Combine(Folder, relative));

        /// <summary>
        /// 이 설정을 명령의 값으로 넣는다. 명령줄에 준 값이 먼저이고, 그다음이 이 파일이다.
        /// 실행 대상은 명령줄에 하나라도 주면(--app, 또는 --package나 --device) 이 파일의 다른 대상은 쓰지 않는다.
        /// </summary>
        public void ApplyTo(Args args)
        {
            if (m_Json.GetValueOrDefault("run") is Dictionary<string, object> run)
                foreach (KeyValuePair<string, object> pair in run)
                    args.Fallback(RunKeys[pair.Key], Convert.ToInt64(pair.Value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture));
            foreach (KeyValuePair<string, string> pair in PathKeys)
                if (m_Json.GetValueOrDefault(pair.Key) is string path && path.Trim().Length > 0)
                    args.Fallback(pair.Value, PathOf(path.Trim()));

            if (Dashboard?.GetValueOrDefault("port") is double port) args.Fallback("listen", ((int)port).ToString(CultureInfo.InvariantCulture));

            bool appGiven = args.Given("app"), androidGiven = args.Given("package") || args.Given("device");
            if (Kind == "pc" && !androidGiven)
            {
                if (TargetValue("app") is string app) args.Fallback("app", PathOf(app));
                if (TargetValue("appArgs") is string appArgs) args.Fallback("app-args", appArgs);
            }
            if (Kind == "android" && !appGiven)
            {
                if (TargetValue("package") is string package) args.Fallback("package", package);
                // 기기를 적지 않았으면 연결된 기기 하나를 쓴다. 기기를 쓰는 명령에서만 찾는다(adb를 부르는 것이 느리다)
                string device = TargetValue("device");
                if (device != null) args.Fallback("device", device);
                else args.Fallback("device", () => Adb.Only(Adb.Parse(Adb.Run(Adb.Find(args.Get("adb", null)), "devices", "-l"))));
            }
        }

        /// <summary>한 부분을 바꿔 둔다. Save를 불러야 파일에 들어간다. 다른 부분은 그대로다.</summary>
        public void Set(string key, object value)
        {
            if (Array.IndexOf(TopKeys, key) < 0) throw new ArgumentException("unknown settings key: " + key);
            m_Json[key] = value;
        }

        public void Save()
        {
            Directory.CreateDirectory(Folder);
            System.IO.File.WriteAllText(File, Qaplay.Json.Write(m_Json, true), new UTF8Encoding(false));
        }

        // ---- qaplay settings: 지금 쓰이는 값과 그 값이 어디서 왔는지, 그리고 문제 ----

        /// <summary>
        /// qaplay settings: 명령줄, 설정 파일, 기본값을 합친 지금의 값을 보여 주고 확인한다(규칙 폴더, PC 빌드 파일, Android 기기와 앱).
        /// 문제가 있으면 종료 코드 1. Unity의 설정 화면이 "확인" 버튼으로 부른다.
        /// </summary>
        public static int Command(Args args, ProjectSettings settings)
        {
            var values = new Dictionary<string, object>();
            var problems = new List<string>();
            void Show(string name, string option, string fallback)
            {
                string value = args.Get(option, null);
                string from = args.Given(option) ? "command line" : value != null ? "settings" : "default";
                values[name] = new Dictionary<string, object> { ["value"] = value ?? fallback, ["from"] = from };
                Console.WriteLine(name + ": " + (value ?? fallback ?? "(none)") + "   (" + from + ")");
            }

            Console.WriteLine("settings file: " + settings.File + (settings.Exists ? "" : " (not there yet: everything below is a default)"));
            string kind = args.Given("app") ? "pc" : args.Given("package") || args.Given("device") ? "android" : settings.Kind ?? "editor";
            values["target"] = kind;
            Console.WriteLine("target: " + kind);
            if (kind == "pc")
            {
                Show("app", "app", null);
                string app = args.Get("app", null);
                if (app == null) problems.Add("the PC build to start is not set (target.app)");
                else if (!System.IO.File.Exists(app)) problems.Add("the PC build is not there: " + app);
            }
            if (kind == "android")
            {
                Show("package", "package", null);
                if (args.Get("package", null) == null) problems.Add("the app's package name is not set (target.package)");
                try
                {
                    Show("device", "device", null);
                    string device = args.Get("device", null), package = args.Get("package", null);
                    if (device != null && package != null)
                    {
                        string installed = Adb.Run(Adb.Find(args.Get("adb", null)), "-s", device, "shell", "pm", "path", package);
                        if (!installed.Contains("package:")) problems.Add("the app " + package + " is not installed on " + device);
                    }
                }
                catch (Exception e) when (e is UsageException || e is IOException)
                {
                    values["device"] = new Dictionary<string, object> { ["value"] = null, ["from"] = "settings" };
                    problems.Add(e.Message);
                }
            }
            var rules = new List<object>();
            foreach (string folder in settings.Rules)
            {
                rules.Add(folder);
                if (!Directory.Exists(folder)) problems.Add("the rules folder is not there: " + folder);
            }
            values["rules"] = rules;
            Console.WriteLine("rules: " + (rules.Count == 0 ? "(none: give rule files to qaplay run)" : string.Join(", ", settings.Rules)));
            Show("retries", "retries", "1");
            Show("freezeSeconds", "freeze-seconds", "10");
            Show("launchSeconds", "launch-seconds", "60");
            Show("port", "port", Program.AgentPort.ToString(CultureInfo.InvariantCulture));
            Show("reports", "reports", Path.GetFullPath(ReportStore.DefaultFolder));
            Show("bugs", "bugs", Path.GetFullPath(BugStore.DefaultFolder));
            string sheet = settings.Sheet == null ? null : settings.Sheet.GetValueOrDefault("type") as string ?? "csv";
            values["sheet"] = sheet;
            Console.WriteLine("sheet: " + (sheet ?? "(not connected)"));

            foreach (string problem in problems) Console.WriteLine("problem: " + problem);
            Console.WriteLine(problems.Count == 0 ? "settings ok" : problems.Count + " problem(s)");
            if (args.Json)
                Program.PrintJson(new Dictionary<string, object>
                {
                    ["file"] = settings.File, ["exists"] = settings.Exists, ["values"] = values, ["problems"] = problems,
                });
            return problems.Count == 0 ? 0 : 1;
        }
    }
}
