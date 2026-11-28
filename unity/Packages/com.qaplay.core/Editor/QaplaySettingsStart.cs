using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;
using static Qaplay.Editor.QaplayUi;

namespace Qaplay.Editor
{
    // 설정 화면의 맨 위 "qaplay": 시작하기. 해야 할 일을 순서대로 보여 주고(되어 있음, 할 일, 골라서), 줄마다 그 페이지로 가는 버튼을 둔다.
    // Unity의 XR Project Validation처럼 무엇이 남았는지를 한눈에 보이게 하고, 고치는 일은 하위 페이지에서 한다
    sealed partial class QaplaySettingsProvider
    {
        enum Mark { Done, Todo, Optional }

        static GUIStyle s_Mark;

        static GUIStyle MarkStyle => s_Mark ??= new GUIStyle(EditorStyles.miniBoldLabel) { alignment = TextAnchor.MiddleCenter };

        // 하위 페이지의 맨 위: 이 페이지에서 무엇을 정하는지
        static void Intro(Page page)
        {
            string text = page switch
            {
                Page.Game => L("Where the game runs and how qaplay drives it: the QA build, the target (editor, PC build, Android), the rules folders and the timing.",
                    "게임을 어디서 돌리고 qaplay가 어떻게 다룰지 정합니다: QA 빌드, 실행 대상(에디터, PC 빌드, Android), 규칙 폴더, 실행 값."),
                Page.Sheet => L("Optional. Connect the sheet where your team writes test cases: qaplay run writes each test case's latest result and a summary there.",
                    "골라서 씁니다. 팀이 TC를 적는 시트를 이으면 qaplay run이 TC마다의 최신 결과와 요약을 거기에 적습니다."),
                Page.Reports => L("Every run is kept as a report (results, numbers, failed screens, game log): where, for how long, and the dashboard to look at them.",
                    "실행마다 리포트(결과, 수치, 실패한 화면, 게임 로그)가 남습니다: 어디에 둘지, 얼마나 둘지, 그리고 그것을 보는 대시보드."),
                Page.Bugs => L("How testers report a bug from the game, where bug reports are kept, and the issue tracker they are sent to.",
                    "테스터가 게임에서 버그를 제보하는 방법, 버그 리포트를 둘 곳, 그리고 버그를 올릴 이슈 관리 도구."),
                Page.Layout => L("Optional. Text that does not fit and elements that overlap on purpose: register them so the layout check skips them.",
                    "필요할 때만. 일부러 그렇게 한 겹침과 넘침을 등록하면 배치 검사가 빼고 봅니다."),
                _ => "",
            };
            EditorGUILayout.HelpBox(text, MessageType.None);
        }

        static void RunnerNeeded()
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.HelpBox(L("This needs the qaplay runner, which was not found. See qaplay (the first page).", "이 부분은 qaplay 러너가 있어야 하는데 찾지 못했습니다. 맨 위 qaplay 페이지를 보세요."), MessageType.Warning);
        }

        // 한 줄: 표시, 할 일, 지금 상태, 그 페이지로 가는 버튼
        static void Row(Mark mark, string what, string state, Action go, string button = null)
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                Color was = GUI.contentColor;
                GUI.contentColor = mark == Mark.Done ? new Color(0.35f, 0.8f, 0.4f) : mark == Mark.Todo ? new Color(1f, 0.65f, 0.2f) : new Color(0.6f, 0.6f, 0.6f);
                GUILayout.Label(mark == Mark.Done ? L("Done", "완료") : mark == Mark.Todo ? L("To do", "할 일") : L("Optional", "선택"), MarkStyle, GUILayout.Width(52), GUILayout.Height(32));
                GUI.contentColor = was;
                using (new EditorGUILayout.VerticalScope())
                {
                    GUILayout.Label(what, EditorStyles.boldLabel);
                    GUILayout.Label(state, EditorStyles.wordWrappedMiniLabel);
                }
                if (go != null && GUILayout.Button(button ?? L("Set up", "설정하기"), GUILayout.Width(100), GUILayout.Height(28))) go();
            }
        }

        void DrawStart()
        {
            EditorGUILayout.HelpBox(L("qaplay tests this game the way a person does: rules tap the screen and check what happens, and the runner (qaplay) reports the results. "
                                      + "Go through the list below from the top. Each line opens its page; the pages are also in the tree on the left (1 to 5).",
                    "qaplay는 사람이 하듯 이 게임을 테스트합니다: 규칙이 화면을 누르고 결과를 확인하며, 러너(qaplay)가 결과를 남깁니다. "
                    + "아래 목록을 위에서부터 따라가세요. 줄마다 그 페이지를 엽니다. 왼쪽 트리의 qaplay 아래(1~5)에도 있습니다."), MessageType.Info);

            // ---- 설정 파일 ----
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.SelectableLabel(L("Settings file: ", "설정 파일: ") + SettingsFile
                                                + (File.Exists(SettingsFile) ? "" : L("   (not there yet: it is made when you change something)", "   (아직 없음: 무엇이든 바꾸면 생깁니다)")),
                    EditorStyles.miniLabel, GUILayout.Height(EditorGUIUtility.singleLineHeight));
            }
            Hint(L("Put it in version control: the runner, CI and your team read the same file.", "저장소에 넣으세요: 러너와 CI와 팀이 같은 파일을 읽습니다."));
            if (m_Error != null)
            {
                EditorGUILayout.HelpBox(L("The settings file cannot be read. Fix it (or delete it to start over):\n", "설정 파일을 읽을 수 없습니다. 고치거나, 지우고 다시 시작하세요:\n") + m_Error, MessageType.Error);
                return;
            }

            Title(L("Getting started", "시작하기"));
            // 0. 러너
            string runner = QaplayRunner.Find();
            Row(runner != null ? Mark.Done : Mark.Todo, L("The qaplay runner", "qaplay 러너"),
                runner ?? L("Not found. Build it in the qaplay folder (dotnet build runner -c Release), or pick qaplay.exe in Preferences > qaplay.",
                    "찾지 못했습니다. qaplay 폴더에서 빌드하거나(dotnet build runner -c Release), Preferences > qaplay에서 qaplay.exe를 고르세요."),
                () => SettingsService.OpenUserPreferences(QaplayPreferences.Path), L("Preferences", "Preferences"));

            // 1. 게임과 실행
            Dictionary<string, object> target = Part(m_Settings, "target");
            string kind = Text(target, "kind");
            if (kind.Length == 0) kind = "editor";
            NamedBuildTarget platform = kind == "android" ? NamedBuildTarget.Android : NamedBuildTarget.Standalone;
            if (kind == "editor")
                Row(Mark.Done, L("1. Where the game runs", "1. 게임을 어디서 돌릴지"), L("In this editor (Play). For a PC build or Android, choose it here.", "이 에디터(Play). PC 빌드나 Android는 여기서 고릅니다."), () => Show(Page.Game));
            else
            {
                string missing = kind == "pc"
                    ? Text(target, "app").Length == 0 ? L("Pick the PC build to start (.exe).", "띄울 PC 빌드(.exe)를 고르세요.")
                    : !File.Exists(Path.Combine(ProjectFolder, Text(target, "app"))) ? L("The PC build is not there: ", "PC 빌드가 없습니다: ") + Text(target, "app") : null
                    : Text(target, "package").Length == 0 ? L("Give the app's package name.", "앱의 패키지 이름을 적으세요.") : null;
                string what = kind == "pc" ? L("PC build: ", "PC 빌드: ") + Text(target, "app") : "Android: " + Text(target, "package");
                Row(missing == null ? Mark.Done : Mark.Todo, L("1. Where the game runs", "1. 게임을 어디서 돌릴지"), missing ?? what, () => Show(Page.Game));
                bool qa = QaBuild(platform);
                Row(qa ? Mark.Done : Mark.Todo, L("1. QA build", "1. QA 빌드"),
                    qa ? L("On for ", "켜짐: ") + (kind == "pc" ? "PC" : "Android") + L(". Turn it off before a release build.", ". 출시 빌드 전에는 끕니다.")
                       : L("Off for ", "꺼짐: ") + (kind == "pc" ? "PC" : "Android") + L(". The build needs the agent: turn it on, then build.", ". 빌드에 에이전트가 들어가야 합니다: 켜고 빌드하세요."),
                    () => Show(Page.Game));
            }
            var rules = new List<string>();
            if (m_Settings.TryGetValue("rules", out object value) && value is List<object> list)
                foreach (object folder in list) rules.Add(folder as string ?? "");
            List<string> gone = rules.FindAll(folder => !Directory.Exists(Path.Combine(ProjectFolder, folder)));
            Row(rules.Count > 0 && gone.Count == 0 ? Mark.Done : Mark.Todo, L("1. Rules folders", "1. 규칙 폴더"),
                rules.Count == 0 ? L("None yet. Add the folder where your rule files are (make rules by recording in qaplay > Rules).", "아직 없습니다. 규칙 파일이 있는 폴더를 더하세요(규칙은 qaplay > Rules 창에서 기록해 만듭니다).")
                : gone.Count > 0 ? L("Not there: ", "없는 폴더: ") + string.Join(", ", gone)
                : string.Join(", ", rules),
                () => Show(Page.Game));

            // 2. TC 시트
            Dictionary<string, object> sheet = Part(m_Settings, "sheet");
            Row(sheet != null ? Mark.Done : Mark.Optional, L("2. Test case sheet", "2. TC 시트"),
                sheet != null ? L("Connected: ", "이어짐: ") + (Text(sheet, "type").Length == 0 ? "csv" : Text(sheet, "type"))
                : L("Not connected: results go to the run reports only. Connect Google Sheets, Excel or CSV if your team keeps test cases there.",
                    "잇지 않음: 결과는 실행 리포트에만 남습니다. 팀이 TC를 Google Sheets, Excel, CSV에 적는다면 잇습니다."),
                () => Show(Page.Sheet));

            // 3. 결과와 리포트
            string reports = Text(m_Settings, "reports");
            Row(Mark.Done, L("3. Results and reports", "3. 결과와 리포트"),
                L("Every run is kept in ", "실행마다 이곳에 남습니다: ") + (reports.Length == 0 ? "qaplay-reports" : reports)
                + (Part(m_Settings, "retention") == null ? L(" (default retention)", " (기본 보관 기한)") : L(" (retention set)", " (보관 기한을 정함)"))
                + L(". The dashboard shows them.", ". 대시보드로 봅니다."),
                () => Show(Page.Reports));

            // 4. 버그
            Dictionary<string, object> issues = Part(m_Settings, "issues");
            Row(issues != null ? Mark.Done : Mark.Optional, L("4. Bugs", "4. 버그"),
                (issues != null ? L("Sent to ", "올리는 곳: ") + Text(issues, "type") + " " + Text(issues, "site") + " " + Text(issues, "project")
                    : L("Kept on this PC only. Connect Jira to send them there.", "이 PC에만 남습니다. Jira를 이으면 거기에도 올라갑니다."))
                + L(". Testers report with F12 or a three-finger hold (changeable).", ". 테스터는 F12나 세 손가락 누르기로 제보합니다(바꿀 수 있음)."),
                () => Show(Page.Bugs));

            // 5. 배치 검사 예외
            Row(Mark.Optional, L("5. Layout check exceptions", "5. 배치 검사 예외"),
                L("Only when the layout check reports overlaps or overflows that are on purpose.", "배치 검사가 알린 겹침이나 넘침이 의도한 것일 때만 등록합니다."),
                () => Show(Page.Layout));

            // ---- 러너로 확인 ----
            EditorGUILayout.Space(8);
            using (new EditorGUI.DisabledScope(runner == null))
                if (GUILayout.Button(L("Check the settings with the runner", "러너로 설정 확인"), GUILayout.Height(26))) Check();
            Hint(L("The runner reads the settings as qaplay run would: the rules folders, the PC build file, the Android device and app.",
                "러너가 qaplay run과 같게 읽어 확인합니다: 규칙 폴더, PC 빌드 파일, Android 기기와 앱."));
            if (m_Checked != null)
            {
                var problems = (List<object>)m_Checked["problems"];
                if (problems.Count == 0) EditorGUILayout.HelpBox(L("Checked: nothing is wrong.", "확인했습니다: 문제가 없습니다."), MessageType.Info);
                else EditorGUILayout.HelpBox(string.Join("\n", problems.ConvertAll(problem => "• " + problem)), MessageType.Warning);
            }

            Title(L("Then", "그다음"));
            Hint(L("Press Play, then run qaplay run in a terminal in this project's folder (it runs the rules folders). To make a rule, open qaplay > Rules and record what you do.",
                "Play를 누르고, 이 프로젝트 폴더의 터미널에서 qaplay run을 실행합니다(규칙 폴더를 돌립니다). 규칙을 만들려면 qaplay > Rules 창을 열고 하는 것을 기록합니다."));
            Hint(L("The language of these pages and where the runner is are set per PC in Preferences > qaplay.", "이 화면의 언어와 러너의 위치는 PC마다 Preferences > qaplay에서 정합니다."));
        }
    }

    /// <summary>Preferences > qaplay: 이 PC에서만의 값(화면의 언어, 러너의 위치). 프로젝트의 설정 파일에는 들어가지 않는다.</summary>
    sealed class QaplayPreferences : SettingsProvider
    {
        public const string Path = "Preferences/qaplay";

        QaplayPreferences() : base(Path, SettingsScope.User, new[] { "qaplay", "language", "runner" })
        {
        }

        [SettingsProvider]
        static SettingsProvider Create() => new QaplayPreferences();

        public override void OnGUI(string searchContext)
        {
            float labelWidth = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = 230;
            using (new EditorGUILayout.VerticalScope(new GUIStyle { padding = new RectOffset(10, 10, 4, 10) }))
            {
                EditorGUILayout.HelpBox(L("These are for this PC only and are not in the project's settings file. The project's settings are in Project Settings > qaplay.",
                    "이 PC에서만 쓰는 값입니다. 프로젝트의 설정 파일에는 들어가지 않습니다. 프로젝트의 설정은 Project Settings > qaplay에 있습니다."), MessageType.None);

                Title(L("Language", "언어"));
                string[] values = { "auto", "en", "ko" };
                int index = Mathf.Max(0, Array.IndexOf(values, Language));
                int picked = EditorGUILayout.Popup(L("Language of qaplay's windows", "qaplay 화면의 언어"), index, new[] { L("Same as this PC", "이 PC와 같게"), "English", "한국어" });
                if (picked != index) Language = values[picked];
                Hint(L("The page names in the tree change after the scripts are reloaded.", "왼쪽 트리의 페이지 이름은 스크립트를 다시 읽은 뒤에 바뀝니다."));

                Title(L("The qaplay runner", "qaplay 러너"));
                string found = QaplayRunner.Find();
                EditorGUILayout.SelectableLabel(found ?? L("Not found", "찾지 못함"), EditorStyles.miniLabel, GUILayout.Height(EditorGUIUtility.singleLineHeight));
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(L("Pick qaplay.exe...", "qaplay.exe 고르기..."), GUILayout.Height(24)))
                    {
                        string file = EditorUtility.OpenFilePanel("qaplay", "", Application.platform == RuntimePlatform.WindowsEditor ? "exe" : "");
                        if (file.Length > 0) QaplayRunner.Chosen = file;
                    }
                    using (new EditorGUI.DisabledScope(QaplayRunner.Chosen.Length == 0))
                        if (GUILayout.Button(L("Find it by itself", "알아서 찾기"), GUILayout.Height(24), GUILayout.Width(130))) QaplayRunner.Chosen = "";
                }
                Hint(L("Without a pick, it is looked for in: the QAPLAY_RUNNER environment variable, the runner build in the folders above this project (when it is inside the qaplay repository), then PATH.",
                    "고르지 않으면 이 순서로 찾습니다: 환경 변수 QAPLAY_RUNNER, 이 프로젝트 위쪽 폴더의 러너 빌드(qaplay 저장소 안에 있을 때), PATH."));
            }
            EditorGUIUtility.labelWidth = labelWidth;
        }
    }
}
