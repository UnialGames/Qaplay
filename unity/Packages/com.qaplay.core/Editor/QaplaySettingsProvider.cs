using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;
using UnityEngine.UIElements;
using static Qaplay.Editor.QaplayUi;

namespace Qaplay.Editor
{
    /// <summary>
    /// qaplay의 설정 화면: Project Settings > qaplay. 이 프로젝트를 어떻게 테스트할지를 한곳에서 정한다.
    /// QA 빌드 켜기, 게임을 어디서 돌릴지(에디터, PC 빌드, Android), 규칙 폴더, 다시 돌리는 횟수 같은 값, 기록을 둘 폴더, TC 시트.
    /// 값은 프로젝트의 qaplay.json에 들어가고(저장소에 넣는다), 러너는 그 파일을 읽는다. QA 빌드 켜기만은 Unity의 Player 설정에 들어간다.
    /// </summary>
    sealed class QaplaySettingsProvider : SettingsProvider
    {
        const string Path_ = "Project/qaplay";
        const string Symbol = "QAPLAY";

        static readonly string[] Kinds = { "editor", "pc", "android" };

        readonly SheetSetupView m_Sheet = new SheetSetupView();
        Dictionary<string, object> m_Settings;
        string m_Error;
        DateTime m_Read;
        List<string> m_Devices;
        string m_DevicesError;
        Dictionary<string, object> m_Checked;

        QaplaySettingsProvider() : base(Path_, SettingsScope.Project, new[] { "qaplay", "QA", "QAPLAY", "rules", "sheet", "Android", "device", "test" })
        {
        }

        [SettingsProvider]
        static SettingsProvider Create() => new QaplaySettingsProvider();

        [MenuItem("qaplay/Settings...", priority = 0)]
        static void Open() => SettingsService.OpenProjectSettings(Path_);

        public override void OnActivate(string searchContext, VisualElement rootElement)
        {
            m_Sheet.OnEnable();
            m_Checked = null;
            Reload();
        }

        // 파일이 바뀌었으면(이 화면이나 러너가 썼으면) 다시 읽는다
        void Reload()
        {
            m_Read = File.Exists(SettingsFile) ? File.GetLastWriteTimeUtc(SettingsFile) : DateTime.MinValue;
            m_Settings = ReadSettings(out m_Error);
        }

        void Change(Action<Dictionary<string, object>> change)
        {
            if (!ChangeSettings(change)) return;
            m_Checked = null;
            Reload();
        }

        static Dictionary<string, object> Part(Dictionary<string, object> json, string name) =>
            json != null && json.TryGetValue(name, out object value) ? value as Dictionary<string, object> : null;

        static string Text(Dictionary<string, object> part, string key) =>
            part != null && part.TryGetValue(key, out object value) && value is string text ? text : "";

        static int Number(Dictionary<string, object> part, string key, int fallback) =>
            part != null && part.TryGetValue(key, out object value) && value is double number ? (int)number : fallback;

        // ---- 화면 ----

        public override void OnGUI(string searchContext)
        {
            DateTime written = File.Exists(SettingsFile) ? File.GetLastWriteTimeUtc(SettingsFile) : DateTime.MinValue;
            if (written != m_Read) Reload();

            float labelWidth = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = 200;
            using (new EditorGUILayout.VerticalScope(new GUIStyle { padding = new RectOffset(10, 10, 4, 10) }))
            {
                if (QaplayRunner.Find() == null) DrawRunnerMissing();
                DrawFile();
                if (m_Settings != null)
                {
                    DrawQaBuild();
                    DrawTarget();
                    DrawRules();
                    DrawRun();
                    DrawFolders();
                    Title(L("Test case sheet", "TC 시트"));
                    if (QaplayRunner.Find() != null) m_Sheet.OnGUI(m_Settings);
                    else Hint(L("Needs the runner (above).", "러너가 있어야 합니다(위)."));
                }
                DrawThisPc();
            }
            EditorGUIUtility.labelWidth = labelWidth;
        }

        static void DrawRunnerMissing()
        {
            EditorGUILayout.HelpBox(L(
                "The qaplay runner was not found. Build it (dotnet build runner -c Release in the qaplay folder), or pick qaplay.exe below.",
                "qaplay 러너를 찾지 못했습니다. qaplay 폴더에서 dotnet build runner -c Release로 빌드하거나, 아래에서 qaplay.exe를 고르세요."), MessageType.Warning);
            if (GUILayout.Button(L("Pick the runner (qaplay.exe)...", "러너(qaplay.exe) 고르기..."), GUILayout.Height(24)))
            {
                string picked = EditorUtility.OpenFilePanel("qaplay", "", Application.platform == RuntimePlatform.WindowsEditor ? "exe" : "");
                if (picked.Length > 0) QaplayRunner.Chosen = picked;
            }
        }

        void DrawFile()
        {
            Hint(L("These settings are kept in the file below. Put it in version control: the runner, CI and your team read the same file. "
                   + "A value given to qaplay on the command line wins over the file.",
                "이 설정은 아래 파일에 들어갑니다. 저장소에 넣으세요: 러너와 CI와 팀이 같은 파일을 읽습니다. qaplay 명령에 직접 준 값이 이 파일보다 먼저입니다."));
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.SelectableLabel(SettingsFile + (File.Exists(SettingsFile) ? "" : L("   (not there yet: it is made when you change something)", "   (아직 없음: 무엇이든 바꾸면 생깁니다)")),
                    EditorStyles.miniLabel, GUILayout.Height(EditorGUIUtility.singleLineHeight));
                using (new EditorGUI.DisabledScope(QaplayRunner.Find() == null || m_Settings == null))
                    if (GUILayout.Button(L("Check the settings", "설정 확인"), GUILayout.Width(130))) Check();
            }
            if (m_Error != null)
                EditorGUILayout.HelpBox(L("The settings file cannot be read, so nothing is changed here until it is fixed:\n", "설정 파일을 읽을 수 없어 고칠 때까지 여기서는 바꾸지 않습니다:\n") + m_Error, MessageType.Error);
            if (m_Checked != null)
            {
                var problems = (List<object>)m_Checked["problems"];
                if (problems.Count == 0) EditorGUILayout.HelpBox(L("Checked: nothing is wrong.", "확인했습니다: 문제가 없습니다."), MessageType.Info);
                else EditorGUILayout.HelpBox(string.Join("\n", problems.ConvertAll(problem => "• " + problem)), MessageType.Warning);
            }
        }

        // 러너가 읽는 그대로 확인한다: 규칙 폴더, PC 빌드 파일, Android 기기와 앱
        void Check()
        {
            QaplayRunner.Result result = Call(L("Checking the settings...", "설정을 확인하는 중..."), "settings", "--json");
            m_Checked = result.json ?? new Dictionary<string, object> { ["problems"] = new List<object> { result.error } };
        }

        // ---- QA 빌드 ----

        static readonly (string label, NamedBuildTarget target)[] Platforms =
        {
            ("PC (Windows, Mac, Linux)", NamedBuildTarget.Standalone), ("Android", NamedBuildTarget.Android), ("iOS", NamedBuildTarget.iOS),
        };

        /// <summary>그 플랫폼의 빌드에 에이전트가 들어가는지(QAPLAY 심벌).</summary>
        public static bool QaBuild(NamedBuildTarget target) =>
            Array.IndexOf(PlayerSettings.GetScriptingDefineSymbols(target).Split(';'), Symbol) >= 0;

        public static void SetQaBuild(NamedBuildTarget target, bool on)
        {
            var symbols = new List<string>(PlayerSettings.GetScriptingDefineSymbols(target).Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries));
            symbols.Remove(Symbol);
            if (on) symbols.Add(Symbol);
            PlayerSettings.SetScriptingDefineSymbols(target, string.Join(";", symbols));
        }

        static void DrawQaBuild()
        {
            Title(L("QA build", "QA 빌드"));
            Hint(L("A QA build carries the qaplay agent, so the runner can drive the game. Turn it on for the platforms you test. "
                   + "Turn it off before you make a release build: a build made while it is on is a QA build. In the editor the agent is always there.",
                "QA 빌드에는 qaplay 에이전트가 들어가 러너가 게임을 다룰 수 있습니다. 테스트하는 플랫폼에서 켜세요. "
                + "출시 빌드를 만들기 전에는 끕니다: 켠 채로 만든 빌드는 QA 빌드입니다. 에디터에서는 늘 에이전트가 있습니다."));
            foreach ((string label, NamedBuildTarget target) in Platforms)
            {
                bool on = QaBuild(target);
                bool now = EditorGUILayout.ToggleLeft(label + (on ? L("   QA build (the agent is in)", "   QA 빌드(에이전트가 들어감)") : ""), on);
                if (now != on) SetQaBuild(target, now);
            }
        }

        // ---- 실행 대상 ----

        void DrawTarget()
        {
            Title(L("Where the game runs", "게임을 어디서 돌릴지"));
            Dictionary<string, object> target = Part(m_Settings, "target");
            string kind = Text(target, "kind");
            int index = Mathf.Max(0, Array.IndexOf(Kinds, kind.Length == 0 ? "editor" : kind));
            int picked = GUILayout.Toolbar(index, new[] { L("Editor (Play)", "에디터(Play)"), L("PC build", "PC 빌드"), "Android" }, GUILayout.Height(24));
            if (picked != index)
            {
                Change(json => Section(json, "target")["kind"] = Kinds[picked]);
                if (Kinds[picked] == "android" && m_Devices == null) RefreshDevices();
                return;
            }

            switch (Kinds[index])
            {
                case "editor":
                    Hint(L("The runner attaches to the game you start with Play in this editor.", "러너가 이 에디터에서 Play로 띄운 게임에 붙습니다."));
                    break;
                case "pc":
                {
                    Hint(L("The runner starts this build, and starts it again after a crash or a freeze.", "러너가 이 빌드를 띄우고, 죽거나 멈추면 다시 띄웁니다."));
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        string app = EditorGUILayout.DelayedTextField(L("Build to start (.exe)", "띄울 빌드(.exe)"), Text(target, "app"));
                        if (app != Text(target, "app")) Change(json => Section(json, "target")["app"] = app.Trim());
                        if (GUILayout.Button(L("Pick...", "고르기..."), GUILayout.Width(70)))
                        {
                            string file = EditorUtility.OpenFilePanel(L("Build to start", "띄울 빌드"), ProjectFolder, Application.platform == RuntimePlatform.WindowsEditor ? "exe" : "");
                            if (file.Length > 0) Change(json => Section(json, "target")["app"] = Relative(file));
                            GUI.FocusControl(null);
                        }
                        string last = EditorUserBuildSettings.GetBuildLocation(BuildTarget.StandaloneWindows64);
                        using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(last)))
                            if (GUILayout.Button(L("Last build", "마지막 빌드"), GUILayout.Width(90)))
                            {
                                Change(json => Section(json, "target")["app"] = Relative(last));
                                GUI.FocusControl(null);
                            }
                    }
                    string args = EditorGUILayout.DelayedTextField(L("Extra arguments", "덧붙일 실행 인자"), Text(target, "appArgs"));
                    if (args != Text(target, "appArgs")) Change(json => Section(json, "target")["appArgs"] = args.Trim());
                    break;
                }
                case "android":
                {
                    Hint(L("The runner installs nothing: install the QA build first. It starts the app, and starts it again after a crash or a freeze.",
                        "러너는 설치하지 않습니다: QA 빌드를 먼저 설치하세요. 러너가 앱을 띄우고, 죽거나 멈추면 다시 띄웁니다."));
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        string package = EditorGUILayout.DelayedTextField(L("App's package name", "앱의 패키지 이름"), Text(target, "package"));
                        if (package != Text(target, "package")) Change(json => Section(json, "target")["package"] = package.Trim());
                        string own = PlayerSettings.GetApplicationIdentifier(NamedBuildTarget.Android);
                        if (GUILayout.Button(L("This project's", "이 프로젝트의 것"), GUILayout.Width(120)))
                        {
                            Change(json => Section(json, "target")["package"] = own);
                            GUI.FocusControl(null);
                        }
                    }
                    DrawDevice(target);
                    break;
                }
            }
        }

        void DrawDevice(Dictionary<string, object> target)
        {
            string device = Text(target, "device");
            var options = new List<string> { L("The one connected (pick it each time)", "연결된 기기 하나(실행할 때마다 찾음)") };
            if (m_Devices != null) options.AddRange(m_Devices);
            if (device.Length > 0 && !options.Contains(device)) options.Add(device);
            using (new EditorGUILayout.HorizontalScope())
            {
                int index = device.Length == 0 ? 0 : options.IndexOf(device);
                int picked = EditorGUILayout.Popup(L("Device", "기기"), index, options.ToArray());
                if (picked != index) Change(json => Section(json, "target")["device"] = picked == 0 ? "" : options[picked]);
                if (GUILayout.Button(L("Refresh", "새로고침"), GUILayout.Width(90))) RefreshDevices();
            }
            Hint(L("Leave it on \"the one connected\" when each PC has its own device: the settings file is shared by the team.",
                "PC마다 기기가 다르면 \"연결된 기기 하나\"로 두세요: 설정 파일은 팀이 함께 씁니다."));
            if (m_DevicesError != null) EditorGUILayout.HelpBox(m_DevicesError, MessageType.Warning);
        }

        void RefreshDevices()
        {
            m_DevicesError = null;
            QaplayRunner.Result result = Call(L("Looking for devices...", "기기를 찾는 중..."), "devices", "--json");
            if (result.json == null)
            {
                m_Devices = new List<string>();
                m_DevicesError = L("The devices could not be listed: ", "기기 목록을 읽지 못했습니다: ") + result.error;
                return;
            }
            m_Devices = new List<string>();
            foreach (object entry in (List<object>)result.json["devices"])
            {
                var device = (Dictionary<string, object>)entry;
                m_Devices.Add((string)device["serial"]);
                if (!"device".Equals(device["state"]))
                    m_DevicesError = device["serial"] + ": " + device["state"]
                                     + L(" (unlock it and allow USB debugging)", " (기기의 잠금을 풀고 USB 디버깅을 허용하세요)");
            }
            if (m_Devices.Count == 0) m_DevicesError = L("No device is connected. Connect one with USB debugging on, or start the emulator.", "연결된 기기가 없습니다. USB 디버깅을 켠 기기를 연결하거나 에뮬레이터를 띄우세요.");
        }

        // ---- 규칙 폴더 ----

        void DrawRules()
        {
            Title(L("Rules folders", "규칙 폴더"));
            Hint(L("qaplay run with no rule given runs these folders. The sheet looks here for the rule of each test case.",
                "qaplay run에 규칙을 주지 않으면 이 폴더들을 돌립니다. 시트는 TC마다의 규칙을 여기서 찾습니다."));
            var rules = new List<string>();
            if (m_Settings.TryGetValue("rules", out object value) && value is List<object> list)
                foreach (object folder in list) rules.Add(folder as string ?? "");
            for (int n = 0; n < rules.Count; n++)
            {
                int at = n;
                using (new EditorGUILayout.HorizontalScope())
                {
                    bool missing = !Directory.Exists(Path.Combine(ProjectFolder, rules[n]));
                    EditorGUILayout.LabelField(rules[n] + (missing ? L("   (not there)", "   (없음)") : ""));
                    if (GUILayout.Button(L("Remove", "빼기"), GUILayout.Width(70)))
                        Change(json =>
                        {
                            var kept = new List<object>(rules.ConvertAll(folder => (object)folder));
                            kept.RemoveAt(at);
                            json["rules"] = kept;
                        });
                }
            }
            if (rules.Count == 0) Hint(L("(none yet)", "(아직 없음)"));
            if (GUILayout.Button(L("Add a folder...", "폴더 더하기..."), GUILayout.Width(140)))
            {
                string picked = EditorUtility.OpenFolderPanel(L("Folder with rule files", "규칙 파일의 폴더"), Application.dataPath, "");
                if (picked.Length > 0 && !rules.Contains(Relative(picked)))
                    Change(json =>
                    {
                        var all = new List<object>(rules.ConvertAll(folder => (object)folder)) { Relative(picked) };
                        json["rules"] = all;
                    });
            }
        }

        // ---- 실행 ----

        void DrawRun()
        {
            Title(L("Running", "실행"));
            Dictionary<string, object> run = Part(m_Settings, "run");
            void Field(string key, string english, string korean, int fallback, string help)
            {
                int now = Number(run, key, fallback);
                int value = Mathf.Max(0, EditorGUILayout.DelayedIntField(new GUIContent(L(english, korean), help), now));
                if (value != now) Change(json => Section(json, "run")[key] = value);
            }
            Field("retries", "Reruns of a failed rule", "실패한 규칙을 다시 돌리는 횟수", 1,
                L("Passing on a rerun is reported as flaky. Crashes, freezes and exceptions are never rerun.", "다시 돌려 통과하면 불안정으로 적습니다. 크래시, 프리징, 예외는 다시 돌리지 않습니다."));
            Field("freezeSeconds", "Seconds without frames = frozen", "프레임이 멈추면 프리징으로 볼 시간(초)", 10, null);
            Field("launchSeconds", "Seconds to wait for the game to start", "게임이 뜨기를 기다리는 시간(초)", 60,
                L("Raise it when the game takes long to start for the first time.", "처음 뜨는 데 오래 걸리는 게임이면 늘립니다."));
            Field("port", "Port", "포트", 27275,
                L("The port the agent in the game listens on. Change it only when another program uses 27275.", "게임 안의 에이전트가 기다리는 포트입니다. 다른 프로그램이 27275를 쓸 때만 바꿉니다."));
        }

        // ---- 기록 ----

        void DrawFolders()
        {
            Title(L("Where records are kept", "기록을 둘 곳"));
            void Folder(string key, string english, string korean, string fallback)
            {
                string now = Text(m_Settings, key);
                using (new EditorGUILayout.HorizontalScope())
                {
                    string value = EditorGUILayout.DelayedTextField(L(english, korean), now.Length == 0 ? fallback : now);
                    if (value != (now.Length == 0 ? fallback : now)) Change(json => json[key] = value.Trim());
                    if (GUILayout.Button(L("Pick...", "고르기..."), GUILayout.Width(70)))
                    {
                        string picked = EditorUtility.OpenFolderPanel(L(english, korean), ProjectFolder, "");
                        if (picked.Length > 0) Change(json => json[key] = Relative(picked));
                        GUI.FocusControl(null);
                    }
                }
            }
            Folder("reports", "Run reports", "실행 리포트", "qaplay-reports");
            Folder("bugs", "Bug reports", "버그 리포트", "qaplay-bugs");
            Hint(L("They grow with every run: keep them out of version control (add them to .gitignore), or on a shared drive for the team.",
                "실행할 때마다 늘어납니다: 저장소에는 넣지 마세요(.gitignore에 더합니다). 팀이 함께 보려면 공유 드라이브에 둡니다."));
        }

        // ---- 이 PC ----

        static void DrawThisPc()
        {
            Title(L("This PC only", "이 PC에서만"));
            string[] values = { "auto", "en", "ko" };
            int index = Mathf.Max(0, Array.IndexOf(values, Language));
            int picked = EditorGUILayout.Popup(L("Language of qaplay's windows", "qaplay 화면의 언어"), index, new[] { L("Same as this PC", "이 PC와 같게"), "English", "한국어" });
            if (picked != index) Language = values[picked];
        }
    }
}
