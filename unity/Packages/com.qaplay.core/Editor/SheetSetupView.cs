using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using static Qaplay.Editor.QaplayUi;

namespace Qaplay.Editor
{
    /// <summary>
    /// 설정 화면(Project Settings > qaplay)의 "TC 시트" 부분. 시트의 종류(Google Sheets, Excel 파일, CSV 파일)를 고르고, 위에서 아래로 따라간다:
    /// 그 종류의 시트가 어디에 있는지 알려 주고, 탭과 칸을 확인하고, "연결"을 누른다. 연결하면 설정 파일(qaplay.json)의 sheet 부분이 된다.
    /// 종류마다 다른 것은 "어디에 있는지를 받는 부분"뿐이다(SheetKind). 나머지 흐름은 종류와 상관없이 같다.
    /// 시트와 이야기하는 일은 러너가 한다(QaplayRunner). 여기는 러너를 부르고 결과를 보여 준다.
    /// </summary>
    sealed class SheetSetupView
    {
        const string PrefPrefix = "qaplay.sheet.";

        internal static string Pref(string name) => EditorPrefs.GetString(PrefPrefix + name + "." + ProjectFolder, "");
        internal static void SetPref(string name, string value) => EditorPrefs.SetString(PrefPrefix + name + "." + ProjectFolder, value);

        /// <summary>
        /// 시트의 종류 하나가 이 화면에서 맡는 부분: 시트가 어디에 있는지를 사람에게서 받고, 그것을 러너의 옵션으로 바꾼다.
        /// 종류를 더하려면 이것을 하나 더 만들어 Kinds에 넣는다(러너 쪽에는 같은 이름의 종류가 있어야 한다).
        /// </summary>
        internal abstract class SheetKind
        {
            /// <summary>러너의 --type에 주는 이름.</summary>
            public abstract string Type { get; }
            public abstract string Label { get; }
            /// <summary>화면이 열릴 때 한 번: 저장해 둔 값을 읽는다.</summary>
            public abstract void Load();
            /// <summary>시트가 어디에 있는지를 받는 부분을 그린다. "확인"을 누를 수 있게 되었으면 true.</summary>
            public abstract bool DrawSource(SheetSetupView view);
            /// <summary>러너에 넘길 옵션: 어디의 시트인지.</summary>
            public abstract void AddSource(List<string> arguments);
            /// <summary>러너가 시트에 닿지 못했을 때 사람에게 보여 줄 말.</summary>
            public abstract void Explain(SheetSetupView view, Dictionary<string, object> failure);
            /// <summary>"어디에 있는지"를 받는 단계의 수. 그다음 단계(탭과 칸)의 번호가 이것 + 1이다.</summary>
            public virtual int Steps => 1;
        }

        // 쓸 수 있는 종류들. 화면은 이 목록만 보고, 어떤 종류가 있는지는 모른다
        static SheetKind[] s_Kinds;

        static SheetKind[] Kinds => s_Kinds ??= new SheetKind[]
        {
            new GoogleKind(),
            new FileKind("xlsx", "Excel", () => L("Excel file", "Excel 파일"), "qa.xlsx",
                () => L("One workbook holds the test cases in one tab and the summary in another. Excel must not have the file open while the tests run.",
                    "통합 문서 하나에 TC 탭과 요약 탭이 들어갑니다. 테스트를 돌리는 동안에는 Excel에서 그 파일을 닫아 두어야 합니다.")),
            new FileKind("csv", "CSV", () => L("CSV file", "CSV 파일"), "tc.csv",
                () => L("A plain text file. The summary goes to summary.csv next to it. It works with any spreadsheet program and with Git.",
                    "글자로 된 파일입니다. 요약은 그 옆의 summary.csv에 쌓입니다. 어떤 표 프로그램으로도 열리고 Git으로 관리하기 좋습니다.")),
        };

        sealed class Tab
        {
            public string title;
            public List<string> header = new List<string>();
            public bool korean;
            // 고른 칸: header의 번호. -1은 "없음"
            public int tc = -1, content = -1, expected = -1;
        }

        int m_Kind;
        // 이어진 상태에서 "다른 시트로 바꾸기"를 눌렀다
        bool m_Changing;
        string m_Problem, m_ProblemDetail;
        List<Tab> m_Tabs;
        int m_Tab, m_Language;
        string m_Done;
        List<string> m_Cases;

        SheetKind Kind => Kinds[m_Kind];

        // 러너가 쓸 수 있는 시트의 종류(qaplay modules). CSV는 러너에 들어 있고, Excel과 Google Sheets는 러너 옆 modules 폴더의 모듈이 더한다.
        // 러너가 없거나 이것을 모르는 러너면 null이고, 그때는 막지 않는다
        List<string> m_Installed;
        string m_ModulesFolder;

        void LoadInstalled()
        {
            m_Installed = null;
            if (QaplayRunner.Find() == null) return;
            QaplayRunner.Result result = QaplayRunner.Run(ProjectFolder, "modules", "--json");
            if (result.json == null || !(result.json.TryGetValue("sheets", out object sheets) && sheets is List<object> types)) return;
            m_Installed = types.ConvertAll(type => type as string);
            m_ModulesFolder = result.json["folder"] as string;
        }

        bool Installed(string type) => m_Installed == null || m_Installed.Contains(type);

        public void OnEnable()
        {
            m_Kind = Mathf.Max(0, Array.FindIndex(Kinds, kind => kind.Type == Pref("kind")));
            m_Language = Korean ? 1 : 0;
            LoadInstalled();
            foreach (SheetKind kind in Kinds) kind.Load();
        }

        // 고른 종류의 모듈이 러너에 없을 때: 무엇이 없고 어디에 넣으면 되는지
        void DrawMissingModule(string type, string label)
        {
            EditorGUILayout.HelpBox(label + L(" comes as a module of the runner, and the runner on this PC does not have it.",
                                        "은(는) 러너에 모듈로 따로 들어가는데, 이 PC의 러너에는 그 모듈이 없습니다."), MessageType.Warning);
            Hint(L("Put the module folder \"" + type + "\" (qaplay." + type + ".dll and the files next to it) into the modules folder next to the runner. "
                   + "The runner comes with its modules folder; when the runner is built from the qaplay repository, the modules are built with it.",
                "러너 옆 modules 폴더에 \"" + type + "\" 모듈 폴더(qaplay." + type + ".dll과 그 옆의 파일들)를 넣으면 됩니다. "
                + "러너를 받을 때 modules 폴더가 함께 옵니다. qaplay 저장소에서 러너를 빌드했다면 모듈도 함께 만들어집니다."));
            if (m_ModulesFolder != null) EditorGUILayout.SelectableLabel(m_ModulesFolder, EditorStyles.miniLabel, GUILayout.Height(16));
            using (new EditorGUILayout.HorizontalScope())
            {
                if (m_ModulesFolder != null && GUILayout.Button(L("Open the modules folder", "modules 폴더 열기"), GUILayout.Height(24)))
                {
                    Directory.CreateDirectory(m_ModulesFolder);
                    EditorUtility.RevealInFinder(m_ModulesFolder);
                }
                if (GUILayout.Button(L("Check again", "다시 확인"), GUILayout.Height(24), GUILayout.Width(110)))
                {
                    LoadInstalled();
                    foreach (SheetKind kind in Kinds) kind.Load();
                }
            }
        }

        internal void Problem(string what, string detail)
        {
            m_Problem = what;
            m_ProblemDetail = detail;
        }

        internal void Forget()
        {
            m_Problem = null;
            m_Tabs = null;
            m_Done = null;
            m_Cases = null;
        }

        // 시트에 닿는지 보고, 그 안의 표와 칸 이름을 받아 온다. 종류와 상관없이 같은 명령이다
        void Probe()
        {
            Forget();
            var arguments = new List<string> { "tc", "tabs", "--type", Kind.Type };
            Kind.AddSource(arguments);
            arguments.Add("--json");
            QaplayRunner.Result result = Call(L("Opening the sheet...", "시트를 여는 중..."), arguments.ToArray());
            if (result.json == null)
            {
                Problem(L("The sheet could not be opened.", "시트를 열지 못했습니다."), result.error);
                return;
            }
            if (!true.Equals(result.json["ok"]))
            {
                Kind.Explain(this, result.json);
                return;
            }
            m_Tabs = new List<Tab>();
            foreach (object entry in (List<object>)result.json["tabs"])
            {
                var data = (Dictionary<string, object>)entry;
                var tab = new Tab { title = (string)data["title"], korean = true.Equals(data["korean"]) };
                foreach (object cell in (List<object>)data["header"]) tab.header.Add((string)cell);
                var columns = (Dictionary<string, object>)data["columns"];
                tab.tc = tab.header.IndexOf(columns["tc"] as string);
                tab.content = tab.header.IndexOf(columns["content"] as string);
                tab.expected = tab.header.IndexOf(columns["expected"] as string);
                m_Tabs.Add(tab);
            }
            // TC 번호로 보이는 칸이 있는 첫 표를 고른다
            m_Tab = Mathf.Max(0, m_Tabs.FindIndex(tab => tab.tc >= 0));
            if (m_Tabs.Count > 0 && m_Tabs[m_Tab].korean) m_Language = 1;
        }

        // 러너가 시트를 확인하고 설정 파일의 sheet 부분에 적는다. 안 되면 쓰던 설정이 그대로 남는다
        void Connect()
        {
            Tab tab = m_Tabs[m_Tab];
            var arguments = new List<string> { "tc", "init", "--settings", SettingsFile, "--replace", "--type", Kind.Type };
            Kind.AddSource(arguments);
            arguments.AddRange(new[] { "--tab", tab.title, "--lang", m_Language == 1 ? "ko" : "en" });
            void Column(string option, int index)
            {
                if (index >= 0) arguments.AddRange(new[] { option, tab.header[index] });
            }
            Column("--tc-column", tab.tc);
            Column("--content-column", tab.content);
            Column("--expected-column", tab.expected);

            QaplayRunner.Result result = Call(L("Connecting the sheet...", "시트를 잇는 중..."), arguments.ToArray());
            if (result.code != 0)
            {
                Problem(L("The sheet could not be connected.", "시트를 잇지 못했습니다."), result.error);
                return;
            }
            SetPref("kind", Kind.Type);
            m_Problem = null;
            m_Changing = false;
            m_Tabs = null;
            m_Done = L("Just connected.", "방금 연결했습니다.");
            LoadCases();
        }

        void LoadCases()
        {
            QaplayRunner.Result result = Call(L("Reading the test cases...", "TC를 읽는 중..."), "tc", "list", "--settings", SettingsFile, "--json");
            if (result.json == null)
            {
                Problem(L("The test cases could not be read.", "TC를 읽지 못했습니다."), result.error);
                return;
            }
            m_Problem = null;
            m_Cases = new List<string>();
            foreach (object entry in (List<object>)result.json["cases"])
            {
                var data = (Dictionary<string, object>)entry;
                m_Cases.Add(data["tc"] + "   " + L("rule: ", "규칙: ") + data["rule"]);
            }
        }

        // 지금 이어져 있는 시트의 종류(설정 파일의 sheet.type). 없으면 null
        static string ConnectedType(Dictionary<string, object> settings) =>
            settings != null && settings.TryGetValue("sheet", out object value) && value is Dictionary<string, object> sheet
                ? sheet.TryGetValue("type", out object type) ? type as string : "csv"
                : null;

        /// <param name="settings">지금의 설정 파일 내용(읽을 수 없으면 null).</param>
        // 두 상태를 나눠 그린다. 이어져 있으면 무엇에 이어졌는지와 할 수 있는 일만, 이어지지 않았으면(또는 바꾸는 중이면) 잇는 단계를 위에서 아래로.
        // 단계마다 순서가 같다: 제목, 무엇을 하는지 한 줄, 입력, 버튼, 그 버튼의 결과
        public void OnGUI(Dictionary<string, object> settings)
        {
            string connectedType = ConnectedType(settings);
            if (connectedType != null && !m_Changing)
            {
                DrawConnected((Dictionary<string, object>)settings["sheet"], connectedType);
                return;
            }
            if (connectedType != null)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.HelpBox(L("Connecting another sheet. The one connected now stays until you press Connect at the end.",
                        "다른 시트로 바꾸는 중입니다. 끝에서 \"연결\"을 누르기 전까지는 지금 이어진 시트가 그대로입니다."), MessageType.None);
                    if (GUILayout.Button(L("Cancel", "취소"), GUILayout.Width(80), GUILayout.Height(38)))
                    {
                        m_Changing = false;
                        Forget();
                        GUIUtility.ExitGUI();
                    }
                }
            }
            else
                EditorGUILayout.HelpBox(L("Not connected. Choose the kind of sheet your team writes test cases in, then go through the steps from 1.",
                    "아직 잇지 않았습니다. 팀이 TC를 적는 시트의 종류를 고르고, 1번부터 차례대로 하세요."), MessageType.None);

            // ---- 종류 ----
            EditorGUILayout.Space(4);
            int kindNow = GUILayout.Toolbar(m_Kind, Array.ConvertAll(Kinds, kind => kind.Label), GUILayout.Height(26));
            if (kindNow != m_Kind)
            {
                m_Kind = kindNow;
                Forget();
                GUI.FocusControl(null);
            }

            if (!Installed(Kind.Type))
            {
                DrawMissingModule(Kind.Type, Kind.Label);
                return;
            }

            // ---- 그 종류의 시트가 어디에 있는지(종류마다 다르다) ----
            bool ready = Kind.DrawSource(this);
            EditorGUILayout.Space(4);
            using (new EditorGUI.DisabledScope(!ready))
                if (GUILayout.Button(L("Check: open the sheet", "확인: 시트 열어 보기"), GUILayout.Height(26))) Probe();

            // ---- 탭과 칸, 연결: 종류와 상관없이 같다 ----
            if (m_Tabs != null && m_Tabs.Count > 0) DrawTabs(Kind.Steps + 1, connectedType != null);

            // 마지막으로 누른 버튼(확인, 연결)의 결과. 그 버튼 바로 아래에 온다
            if (m_Problem != null)
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.HelpBox(m_Problem + "\n" + m_ProblemDetail, MessageType.Error);
            }
        }

        void DrawTabs(int step, bool replacing)
        {
            Title(step + L(". Tab and columns", ". 탭과 칸"));
            Hint(L("The sheet opened. Check the tab and the columns found, then press Connect. The tool adds its own columns (rule status, latest result, ...) after yours and never changes the columns you wrote.",
                "시트를 열었습니다. 찾은 탭과 칸을 확인하고 \"연결\"을 누르세요. 도구는 사람이 적은 칸 뒤에 자기 칸(규칙 상태, 최신 결과 등)을 더하고, 사람이 적은 칸은 고치지 않습니다."));
            m_Tab = EditorGUILayout.Popup(L("Tab with the test cases", "TC가 있는 탭"), m_Tab, m_Tabs.ConvertAll(tab => tab.title).ToArray());
            Tab tab = m_Tabs[m_Tab];
            if (tab.header.Count == 0)
                EditorGUILayout.HelpBox(L("This tab is empty. When you connect, the column names (TC, Steps, Expected) are written in it; then write your test cases under them.",
                    "이 탭은 비어 있습니다. 연결하면 칸 이름(TC 번호, 내용, 기대 결과)을 적어 드립니다. 그 아래에 TC를 적으면 됩니다."), MessageType.None);
            else
            {
                var options = new List<string> { L("(none)", "(없음)") };
                options.AddRange(tab.header);
                string[] names = options.ToArray();
                tab.tc = EditorGUILayout.Popup(L("Column: test case number", "칸: TC 번호"), tab.tc + 1, names) - 1;
                tab.content = EditorGUILayout.Popup(L("Column: what to do", "칸: 내용(스텝)"), tab.content + 1, names) - 1;
                tab.expected = EditorGUILayout.Popup(L("Column: expected result", "칸: 기대 결과"), tab.expected + 1, names) - 1;
                if (tab.tc < 0) EditorGUILayout.HelpBox(L("Choose the column that holds the test case number.", "TC 번호가 적힌 칸을 고르세요."), MessageType.Warning);
            }
            m_Language = EditorGUILayout.Popup(L("Words the tool writes", "도구가 적는 말"), m_Language, new[] { "English (passed, failed)", "한국어 (통과, 실패)" });
            EditorGUILayout.Space(4);
            using (new EditorGUI.DisabledScope(tab.header.Count > 0 && tab.tc < 0))
                if (GUILayout.Button(replacing ? L("Connect this sheet instead", "이 시트로 바꿔 연결") : L("Connect", "연결"), GUILayout.Height(30))) Connect();
        }

        // 이어진 상태: 무엇에 이어졌는지, 할 수 있는 일, TC 목록
        void DrawConnected(Dictionary<string, object> sheet, string type)
        {
            SheetKind kind = Array.Find(Kinds, k => k.Type == type);
            string Get(string key) => sheet.TryGetValue(key, out object value) ? value as string : null;
            string Column(string key, string fallback) =>
                sheet.TryGetValue("columns", out object value) && value is Dictionary<string, object> columns && columns.TryGetValue(key, out object name) ? name as string : fallback;
            bool korean = sheet.TryGetValue("labels", out object labels) && labels is Dictionary<string, object> words && "통과".Equals(words.GetValueOrDefault("passed"));
            string where = Get("spreadsheet") ?? Get("file") ?? Get("tc");

            var lines = new List<string>
            {
                (m_Done != null ? m_Done + " " : "") + L("Connected: ", "연결되어 있습니다: ") + (kind?.Label ?? type),
                L("Sheet: ", "시트: ") + where,
                L("Test cases: ", "TC 표: ") + Get("tc") + (Get("summary") != null ? L("    Summary: ", "    요약 표: ") + Get("summary") : ""),
                L("Columns: ", "칸: ") + L("test case ", "TC 번호 ") + Column("tc", "TC") + ", " + L("steps ", "내용 ") + Column("content", "Steps") + ", " + L("expected ", "기대 결과 ") + Column("expected", "Expected"),
                L("Words the tool writes: ", "도구가 적는 말: ") + (korean ? "한국어" : "English"),
                L("qaplay run writes each test case's latest result there, and a row in the summary.", "qaplay run을 돌리면 TC마다의 최신 결과와 요약 한 줄이 여기에 적힙니다."),
            };
            EditorGUILayout.HelpBox(string.Join("\n", lines), MessageType.Info);
            if (!Installed(type))
                EditorGUILayout.HelpBox(L("The runner on this PC does not have the module for this sheet, so it cannot read the settings. Press Change sheet and choose that kind to see what to do.",
                    "이 PC의 러너에 이 시트의 모듈이 없어 러너가 설정을 읽지 못합니다. \"다른 시트로 바꾸기\"에서 그 종류를 고르면 할 일이 나옵니다."), MessageType.Error);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(L("Read the test cases", "TC 불러오기"), GUILayout.Height(24))) LoadCases();
                if (GUILayout.Button(L("Open the sheet", "시트 열기"), GUILayout.Height(24))) OpenConnected(type, where);
                if (GUILayout.Button(L("Change sheet...", "다른 시트로 바꾸기..."), GUILayout.Height(24)))
                {
                    m_Changing = true;
                    m_Done = null;
                    m_Kind = Mathf.Max(0, Array.FindIndex(Kinds, k => k.Type == type));
                    Forget();
                }
                if (GUILayout.Button(L("Disconnect...", "연결 끊기..."), GUILayout.Height(24))
                    && EditorUtility.DisplayDialog("qaplay", L("Disconnect the sheet? The sheet itself is not changed; results just stop going there.",
                        "시트 연결을 끊을까요? 시트 자체는 그대로이고, 결과가 더는 거기에 적히지 않습니다."), L("Disconnect", "끊기"), L("Cancel", "취소")))
                {
                    ChangeSettings(json => json.Remove("sheet"));
                    m_Done = null;
                    m_Cases = null;
                    GUIUtility.ExitGUI();
                }
            }

            if (m_Problem != null) EditorGUILayout.HelpBox(m_Problem + "\n" + m_ProblemDetail, MessageType.Error);
            if (m_Cases != null)
            {
                Title(L("Test cases", "TC 목록"));
                Hint(m_Cases.Count == 0
                    ? L("No test cases yet. Write them in the sheet, one per row.", "아직 TC가 없습니다. 시트에 한 줄에 하나씩 적으세요.")
                    : m_Cases.Count + L(" test case(s)", "개의 TC"));
                foreach (string line in m_Cases) EditorGUILayout.LabelField(line);
            }

            Title(L("Linking a rule to a test case", "규칙과 TC 잇기"));
            Hint(L("Write the test case number at the top of the rule file: tc: TC-012. The sheet's rule status is looked up in the rules folders (page 1).",
                "규칙 파일의 머리말에 TC 번호를 적습니다: tc: TC-012. 시트의 규칙 상태는 규칙 폴더(1번 페이지)에서 찾습니다."));
        }

        static void OpenConnected(string type, string where)
        {
            if (string.IsNullOrEmpty(where)) return;
            if (type == "google") Application.OpenURL(where);
            else
            {
                string file = Path.GetFullPath(Path.Combine(ProjectFolder, where));
                if (File.Exists(file)) EditorUtility.OpenWithDefaultApp(file);
            }
        }

        // ---- 종류: Google Sheets ----

        sealed class GoogleKind : SheetKind
        {
            const string ApiPage = "https://console.cloud.google.com/apis/library/sheets.googleapis.com";
            const string CreateAccountPage = "https://console.cloud.google.com/iam-admin/serviceaccounts/create";
            const string AccountsPage = "https://console.cloud.google.com/iam-admin/serviceaccounts";
            const string NewSheetPage = "https://sheets.new";

            string m_Email, m_KeyFile, m_Address = "";

            public override string Type => "google";
            public override string Label => "Google Sheets";
            public override int Steps => 2;

            public override void Load()
            {
                m_Address = Pref("address");
                ReadKey();
            }

            void ReadKey()
            {
                m_Email = null;
                if (QaplayRunner.Find() == null) return;
                QaplayRunner.Result result = QaplayRunner.Run(ProjectFolder, "tc", "key", "--json");
                if (result.json == null) return;
                m_Email = result.json["email"] as string;
                m_KeyFile = result.json["file"] as string;
            }

            void InstallKey(SheetSetupView view, string file)
            {
                QaplayRunner.Result result = Call(L("Checking the key...", "키를 확인하는 중..."), "tc", "key", "--credentials", file, "--json");
                if (result.code != 0)
                {
                    view.Problem(L("That file is not a Google service account key.", "그 파일은 Google 서비스 계정의 키가 아닙니다."),
                        L("Pick the .json file that was downloaded in step 3 (Keys > Add key > Create new key > JSON).",
                            "3번에서 내려받은 .json 파일을 고르세요(키 > 키 추가 > 새 키 만들기 > JSON).") + "\n" + result.error);
                    return;
                }
                view.Forget();
                ReadKey();
            }

            public override bool DrawSource(SheetSetupView view)
            {
                Title(L("1. Google key", "1. Google 키"));
                if (m_Email == null)
                {
                    Hint(L("The runner signs in to Google with a key file. Make one in Google Cloud (free, once per PC). The key is copied to your user folder (.qaplay), never into the project.",
                        "러너는 키 파일로 Google에 로그인합니다. Google Cloud에서 하나 만듭니다(무료, PC마다 한 번). 키는 사용자 폴더(.qaplay)로 복사되고 프로젝트에는 들어가지 않습니다."));
                    Step(L("1. Enable the Google Sheets API (press Enable on that page). Create a project first if it asks.",
                            "1. Google Sheets API를 켭니다(그 페이지에서 \"사용\"). 프로젝트가 없다고 하면 먼저 하나 만듭니다."),
                        L("Open: enable the API", "열기: Sheets API 켜기"), ApiPage);
                    Step(L("2. Create a service account. Type any name and press Done; it needs no roles.",
                            "2. 서비스 계정을 만듭니다. 이름만 아무것이나 적고 \"완료\"를 누릅니다. 역할은 주지 않아도 됩니다."),
                        L("Open: create account", "열기: 계정 만들기"), CreateAccountPage);
                    Step(L("3. Click the account you made > Keys > Add key > Create new key > JSON > Create. A .json file is downloaded.",
                            "3. 만든 계정을 누르고 > \"키\" > \"키 추가\" > \"새 키 만들기\" > JSON > \"만들기\". .json 파일이 내려받아집니다."),
                        L("Open: accounts", "열기: 계정 목록"), AccountsPage);
                    EditorGUILayout.Space(4);
                    if (GUILayout.Button(L("4. Pick the downloaded key file (.json)...", "4. 내려받은 키 파일(.json) 고르기..."), GUILayout.Height(30)))
                    {
                        string downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                        string picked = EditorUtility.OpenFilePanel(L("Google key file", "Google 키 파일"), Directory.Exists(downloads) ? downloads : "", "json");
                        if (picked.Length > 0) InstallKey(view, picked);
                    }
                    return false;
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.HelpBox(L("Ready. The runner signs in as ", "준비됨. 러너는 이 계정으로 로그인합니다: ") + m_Email, MessageType.Info);
                    if (GUILayout.Button(L("Use another key...", "다른 키로 바꾸기..."), GUILayout.Width(150), GUILayout.Height(38)))
                    {
                        string picked = EditorUtility.OpenFilePanel(L("Google key file", "Google 키 파일"), "", "json");
                        if (picked.Length > 0) InstallKey(view, picked);
                    }
                }

                Title(L("2. The sheet", "2. 시트"));
                Hint(L("Tell qaplay which Google sheet holds the test cases, and let the runner's account edit it.", "TC가 적힌 Google 시트를 알려 주고, 러너의 계정이 그 시트를 고칠 수 있게 합니다."));
                Step(L("a. Open the sheet in a browser. If you have none, make a new empty one.", "가. 시트를 브라우저로 엽니다. 없으면 빈 시트를 새로 만듭니다."),
                    L("Open: new sheet", "열기: 새 시트 만들기"), NewSheetPage);
                Hint(L("b. Copy the sheet's address from the browser's address bar and paste it here:", "나. 브라우저의 주소 칸에서 시트의 주소를 복사해 여기에 붙여 넣습니다."));
                using (new EditorGUILayout.HorizontalScope())
                {
                    string address = EditorGUILayout.TextField(m_Address);
                    if (GUILayout.Button(L("Paste", "붙여넣기"), GUILayout.Width(70)))
                    {
                        address = EditorGUIUtility.systemCopyBuffer.Trim();
                        GUI.FocusControl(null);
                    }
                    if (address != m_Address)
                    {
                        m_Address = address;
                        SetPref("address", address);
                    }
                }
                Hint(L("c. In the sheet press Share, paste this account, choose Editor and press Send:", "다. 시트에서 \"공유\"를 누르고, 이 계정을 붙여 넣고, \"편집자\"를 고른 뒤 \"전송\"을 누릅니다."));
                Copyable(m_Email);
                return m_Address.Trim().Length > 0;
            }

            public override void AddSource(List<string> arguments)
            {
                arguments.Add("--spreadsheet");
                arguments.Add(m_Address.Trim());
            }

            public override void Explain(SheetSetupView view, Dictionary<string, object> failure)
            {
                double status = Convert.ToDouble(failure["status"]);
                string message = (string)failure["message"];
                // 러너가 Google이 붙인 까닭으로 가려 준 것을 쓴다(글을 뒤지면 안내 문구의 낱말에 속는다)
                string problem = failure.TryGetValue("problem", out object value) ? value as string : null;
                if (status == 404 || problem == "not_found")
                    view.Problem(L("There is no sheet at that address.", "그 주소에 시트가 없습니다."),
                        L("Open the sheet in a browser and copy the whole address from the address bar.", "브라우저로 시트를 열고 주소 칸의 주소를 통째로 복사해 붙여 넣으세요.") + "\n" + message);
                else if (problem == "api_disabled")
                    view.Problem(L("The Google Sheets API is not enabled for the key's project.", "키의 프로젝트에서 Google Sheets API가 켜져 있지 않습니다."),
                        L("Open the address in the message, press Enable, wait a minute and check again.",
                            "메시지에 있는 주소를 열어 \"사용\"을 누르고, 1분쯤 뒤에 다시 확인하세요.") + "\n" + message);
                else
                    view.Problem(L("The sheet is not shared with the runner's account yet.", "시트가 아직 러너의 계정에 공유되어 있지 않습니다."),
                        L("Do step c above (share the sheet with this account as an Editor), then press Check again: ",
                            "위의 \"다\"를 하세요(이 계정을 \"편집자\"로 공유). 그다음 \"확인\"을 다시 누르세요: ") + m_Email + "\n"
                        + L("Already shared? Check that the account is exactly this one and the role is Editor. A company Google account may block sharing outside the company: then ask its admin.",
                            "이미 공유했다면: 계정이 정확히 이것인지, 역할이 \"편집자\"인지 보세요. 회사 Google 계정은 회사 밖 공유를 막을 수 있습니다. 그때는 관리자에게 물어보세요."));
            }

        }

        // ---- 종류: 파일(Excel, CSV). 둘은 확장자와 설명만 다르다 ----

        sealed class FileKind : SheetKind
        {
            readonly string m_Type, m_Label, m_DefaultFile;
            readonly Func<string> m_Name, m_About;
            string m_File = "";

            public FileKind(string type, string label, Func<string> name, string defaultFile, Func<string> about)
            {
                m_Type = type;
                m_Label = label;
                m_Name = name;
                m_DefaultFile = defaultFile;
                m_About = about;
            }

            public override string Type => m_Type;
            public override string Label => m_Label;

            public override void Load() => m_File = Pref("file." + m_Type);

            void Set(SheetSetupView view, string file)
            {
                m_File = file;
                SetPref("file." + m_Type, file);
                view.Forget();
            }

            public override bool DrawSource(SheetSetupView view)
            {
                Title("1. " + m_Name());
                Hint(m_About() + " " + L("Pick the file your team already uses, or make a new one (it is created with the column names when you connect). "
                                         + "Keep it where the whole team can reach it: in the project (shared through Git) or on a shared drive.",
                    "팀이 쓰던 파일을 고르거나 새로 만듭니다(새 파일은 연결할 때 칸 이름과 함께 만들어집니다). 팀이 함께 볼 수 있는 곳에 둡니다: 프로젝트 안(Git으로 공유)이나 공유 드라이브."));
                using (new EditorGUILayout.HorizontalScope())
                {
                    string file = EditorGUILayout.TextField(m_File);
                    if (file != m_File) Set(view, file);
                    if (GUILayout.Button(L("Pick...", "고르기..."), GUILayout.Width(70)))
                    {
                        string picked = EditorUtility.OpenFilePanel(m_Name(), ProjectFolder, m_Type);
                        if (picked.Length > 0) Set(view, Relative(picked));
                        GUI.FocusControl(null);
                    }
                    if (GUILayout.Button(L("New...", "새로 만들기..."), GUILayout.Width(100)))
                    {
                        string picked = EditorUtility.SaveFilePanel(m_Name(), ProjectFolder, m_DefaultFile, m_Type);
                        if (picked.Length > 0) Set(view, Relative(picked));
                        GUI.FocusControl(null);
                    }
                }
                return m_File.Trim().Length > 0;
            }

            public override void AddSource(List<string> arguments)
            {
                arguments.Add("--file");
                arguments.Add(m_File.Trim());
            }

            public override void Explain(SheetSetupView view, Dictionary<string, object> failure) =>
                view.Problem(L("The file could not be opened.", "파일을 열지 못했습니다."), (string)failure["message"]);

        }
    }
}
