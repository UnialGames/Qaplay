using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Qaplay.Editor
{
    /// <summary>
    /// TC 시트를 잇는 창(qaplay > Sheet). 먼저 시트의 종류(Google Sheets, Excel 파일, CSV 파일)를 고르고, 위에서 아래로 따라간다:
    /// 그 종류의 시트가 어디에 있는지 알려 주고, 탭과 칸을 확인하고, "연결"을 누른다.
    /// 종류마다 다른 것은 "어디에 있는지를 받는 부분"뿐이다(SheetKind). 나머지 화면과 흐름은 종류와 상관없이 같다.
    /// 시트와 이야기하는 일은 러너가 한다(QaplayRunner). 이 창은 러너를 부르고 결과를 보여 준다.
    /// </summary>
    sealed class SheetWindow : EditorWindow
    {
        const string PrefPrefix = "qaplay.sheet.";

        // 화면의 말은 PC의 언어를 따른다: 한국어면 한국어, 아니면 영어
        static readonly bool Korean = Application.systemLanguage == SystemLanguage.Korean;
        internal static string L(string english, string korean) => Korean ? korean : english;

        internal static string ProjectFolder => Directory.GetParent(Application.dataPath).FullName;
        static string SettingsFile => Path.Combine(ProjectFolder, "qaplay-sheet.json");

        internal static string Pref(string name) => EditorPrefs.GetString(PrefPrefix + name + "." + ProjectFolder, "");
        internal static void SetPref(string name, string value) => EditorPrefs.SetString(PrefPrefix + name + "." + ProjectFolder, value);

        /// <summary>
        /// 시트의 종류 하나가 이 창에서 맡는 부분: 시트가 어디에 있는지를 사람에게서 받고, 그것을 러너의 옵션으로 바꾼다.
        /// 종류를 더하려면 이것을 하나 더 만들어 Kinds에 넣는다(러너 쪽에는 같은 이름의 종류가 있어야 한다).
        /// </summary>
        internal abstract class SheetKind
        {
            /// <summary>러너의 --type에 주는 이름.</summary>
            public abstract string Type { get; }
            public abstract string Label { get; }
            /// <summary>창이 열릴 때 한 번: 저장해 둔 값을 읽는다.</summary>
            public abstract void Load(SheetWindow window);
            /// <summary>시트가 어디에 있는지를 받는 부분을 그린다. "확인"을 누를 수 있게 되었으면 true.</summary>
            public abstract bool DrawSource(SheetWindow window);
            /// <summary>러너에 넘길 옵션: 어디의 시트인지.</summary>
            public abstract void AddSource(List<string> arguments);
            /// <summary>러너가 시트에 닿지 못했을 때 사람에게 보여 줄 말.</summary>
            public abstract void Explain(SheetWindow window, Dictionary<string, object> failure);
            /// <summary>이어진 시트를 여는 버튼이 할 일.</summary>
            public abstract void OpenSheet();
        }

        // 쓸 수 있는 종류들. 창은 이 목록만 보고, 어떤 종류가 있는지는 모른다
        static readonly SheetKind[] Kinds =
        {
            new GoogleKind(),
            new FileKind("xlsx", "Excel", L("Excel file", "Excel 파일"), "qa.xlsx",
                L("One workbook holds the test cases in one tab and the summary in another. Excel must not have the file open while the tests run.",
                    "통합 문서 하나에 TC 탭과 요약 탭이 들어갑니다. 테스트를 돌리는 동안에는 Excel에서 그 파일을 닫아 두어야 합니다.")),
            new FileKind("csv", "CSV", L("CSV file", "CSV 파일"), "tc.csv",
                L("A plain text file. The summary goes to summary.csv next to it. It works with any spreadsheet program and with Git.",
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
        string m_Problem, m_ProblemDetail;
        List<Tab> m_Tabs;
        int m_Tab, m_Language;
        string m_Rules = "";
        string m_Done;
        List<string> m_Cases;
        Vector2 m_Page;

        SheetKind Kind => Kinds[m_Kind];

        [MenuItem("qaplay/Sheet")]
        static void Open()
        {
            var window = GetWindow<SheetWindow>("qaplay Sheet");
            window.minSize = new Vector2(480, 440);
        }

        void OnEnable()
        {
            m_Kind = Mathf.Max(0, Array.FindIndex(Kinds, kind => kind.Type == Pref("kind")));
            m_Language = Korean ? 1 : 0;
            m_Rules = GuessRules();
            foreach (SheetKind kind in Kinds) kind.Load(this);
        }

        // 규칙 파일(.yaml)이 가장 많이 든 Assets 아래의 폴더를 규칙 폴더로 짐작한다. 틀리면 사람이 고친다
        static string GuessRules()
        {
            try
            {
                var count = new Dictionary<string, int>();
                foreach (string file in Directory.GetFiles(Application.dataPath, "*.yaml", SearchOption.AllDirectories))
                {
                    string folder = Path.GetDirectoryName(file);
                    count[folder] = count.TryGetValue(folder, out int n) ? n + 1 : 1;
                }
                string best = null;
                foreach (KeyValuePair<string, int> pair in count)
                    if (best == null || pair.Value > count[best]) best = pair.Key;
                return best == null ? "" : Relative(best);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return "";
            }
        }

        // 프로젝트 안의 경로는 프로젝트 기준으로 적는다(설정 파일을 다른 PC로 옮겨도 맞게). 밖이면 전체 경로다
        internal static string Relative(string path)
        {
            string relative = Path.GetRelativePath(ProjectFolder, path);
            return (relative.StartsWith("..") || Path.IsPathRooted(relative) ? path : relative).Replace('\\', '/');
        }

        // ---- 러너 부르기 ----

        internal QaplayRunner.Result Call(string busy, params string[] arguments)
        {
            EditorUtility.DisplayProgressBar("qaplay", busy, 0.5f);
            try
            {
                return QaplayRunner.Run(ProjectFolder, arguments);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
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

        void Connect()
        {
            Tab tab = m_Tabs[m_Tab];
            // 새 설정은 옆의 파일에 먼저 쓴다. 잇는 데 실패하면 쓰던 설정이 그대로 남는다
            string fresh = SettingsFile + ".new";
            if (File.Exists(fresh)) File.Delete(fresh);
            var arguments = new List<string> { "tc", "init", "--sheet", fresh, "--type", Kind.Type };
            Kind.AddSource(arguments);
            arguments.AddRange(new[] { "--tab", tab.title, "--lang", m_Language == 1 ? "ko" : "en" });
            if (m_Rules.Trim().Length > 0) arguments.AddRange(new[] { "--rules", m_Rules.Trim() });
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
                if (File.Exists(fresh)) File.Delete(fresh);
                Problem(L("The sheet could not be connected.", "시트를 잇지 못했습니다."), result.error);
                return;
            }
            File.Copy(fresh, SettingsFile, true);
            File.Delete(fresh);
            SetPref("kind", Kind.Type);
            m_Problem = null;
            m_Done = L("Connected. The settings are in ", "연결했습니다. 설정 파일: ") + SettingsFile;
            LoadCases();
        }

        void LoadCases()
        {
            QaplayRunner.Result result = Call(L("Reading the test cases...", "TC를 읽는 중..."), "tc", "list", "--sheet", SettingsFile, "--json");
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

        // 지금 이어져 있는 시트의 종류(설정 파일의 type). 없거나 읽을 수 없으면 null
        static string ConnectedType()
        {
            try
            {
                if (!File.Exists(SettingsFile)) return null;
                var json = Json.Parse(File.ReadAllText(SettingsFile)) as Dictionary<string, object>;
                return json != null && json.TryGetValue("type", out object type) ? type as string : "csv";
            }
            catch (Exception e) when (e is IOException || e is FormatException)
            {
                return null;
            }
        }

        // ---- 화면 ----

        // 긴 글은 창의 폭에 맞춰 여러 줄로 꺾인다
        internal static void Hint(string text) => GUILayout.Label(text, EditorStyles.wordWrappedMiniLabel);

        internal static void Title(string text)
        {
            EditorGUILayout.Space(10);
            GUILayout.Label(text, EditorStyles.boldLabel);
        }

        // 한 줄: 설명과, 그 페이지를 여는 버튼
        internal static void Step(string text, string button, string url)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(text, EditorStyles.wordWrappedLabel);
                if (GUILayout.Button(button, GUILayout.Width(150))) Application.OpenURL(url);
            }
        }

        internal static void Copyable(string text)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.SelectableLabel(text, EditorStyles.textField, GUILayout.Height(EditorGUIUtility.singleLineHeight));
                if (GUILayout.Button(L("Copy", "복사"), GUILayout.Width(60))) EditorGUIUtility.systemCopyBuffer = text;
            }
        }

        void OnGUI()
        {
            m_Page = EditorGUILayout.BeginScrollView(m_Page);

            if (QaplayRunner.Find() == null)
            {
                EditorGUILayout.HelpBox(L(
                    "The qaplay runner was not found. Build it (dotnet build runner -c Release in the qaplay folder), or pick qaplay.exe below.",
                    "qaplay 러너를 찾지 못했습니다. qaplay 폴더에서 dotnet build runner -c Release로 빌드하거나, 아래에서 qaplay.exe를 고르세요."), MessageType.Warning);
                if (GUILayout.Button(L("Pick the runner (qaplay.exe)...", "러너(qaplay.exe) 고르기..."), GUILayout.Height(26)))
                {
                    string picked = EditorUtility.OpenFilePanel("qaplay", "", Application.platform == RuntimePlatform.WindowsEditor ? "exe" : "");
                    if (picked.Length > 0)
                    {
                        QaplayRunner.Chosen = picked;
                        foreach (SheetKind kind in Kinds) kind.Load(this);
                    }
                }
                EditorGUILayout.EndScrollView();
                return;
            }

            string connectedType = ConnectedType();
            bool connected = connectedType != null;
            if (connected)
            {
                SheetKind now = Array.Find(Kinds, kind => kind.Type == connectedType);
                EditorGUILayout.HelpBox(L("This project is connected to a sheet", "이 프로젝트는 시트에 이어져 있습니다") + " (" + (now == null ? connectedType : now.Label) + "). "
                                        + L("qaplay run writes its results there when it is run in:\n", "아래 폴더에서 qaplay run을 돌리면 결과가 그 시트에 적힙니다.\n") + ProjectFolder, MessageType.Info);
            }
            else
                Hint(L("Choose the kind of sheet your team uses, then follow the steps from top to bottom.", "팀이 쓰는 시트의 종류를 고른 뒤, 위에서 아래로 차례대로 하면 됩니다."));

            // ---- 종류 ----
            Title(L("Kind of sheet", "시트의 종류"));
            int kindNow = GUILayout.Toolbar(m_Kind, Array.ConvertAll(Kinds, kind => kind.Label), GUILayout.Height(26));
            if (kindNow != m_Kind)
            {
                m_Kind = kindNow;
                Forget();
                GUI.FocusControl(null);
            }

            // ---- 그 종류의 시트가 어디에 있는지 ----
            bool ready = Kind.DrawSource(this);
            using (new EditorGUI.DisabledScope(!ready))
                if (GUILayout.Button(L("Check", "확인"), GUILayout.Height(26))) Probe();

            // ---- 탭과 칸: 종류와 상관없이 같다 ----
            if (m_Tabs != null && m_Tabs.Count > 0)
            {
                Title(L("Tab and columns", "탭과 칸"));
                EditorGUILayout.HelpBox(L("The sheet opened. Check what was found, then press Connect.", "시트가 열렸습니다. 찾은 내용을 확인하고 \"연결\"을 누르세요."), MessageType.Info);
                if (m_Tabs.Count > 1)
                    m_Tab = EditorGUILayout.Popup(L("Tab with the test cases", "TC가 있는 탭"), m_Tab, m_Tabs.ConvertAll(tab => tab.title).ToArray());
                Tab tab = m_Tabs[m_Tab];
                if (tab.header.Count == 0)
                    Hint(L("It is empty. The column names (TC, Steps, Expected) will be written for you; then write your test cases under them.",
                        "비어 있습니다. 칸 이름(TC 번호, 내용, 기대 결과)을 적어 드립니다. 그 아래에 TC를 적으면 됩니다."));
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
                using (new EditorGUILayout.HorizontalScope())
                {
                    m_Rules = EditorGUILayout.TextField(L("Folder with the rules", "규칙 파일의 폴더"), m_Rules);
                    if (GUILayout.Button(L("Pick...", "고르기..."), GUILayout.Width(70)))
                    {
                        string picked = EditorUtility.OpenFolderPanel(L("Folder with the rule files", "규칙 파일의 폴더"), Application.dataPath, "");
                        if (picked.Length > 0) m_Rules = Relative(picked);
                    }
                }
                Hint(L("The tool adds its own columns (rule status, latest result, ...) after yours. It never changes the columns you wrote.",
                    "도구는 자기 칸(규칙 상태, 최신 결과 등)을 뒤에 더합니다. 사람이 적은 칸은 고치지 않습니다."));
                using (new EditorGUI.DisabledScope(tab.header.Count > 0 && tab.tc < 0))
                    if (GUILayout.Button(connected ? L("Connect this sheet instead", "이 시트로 바꿔 연결") : L("Connect", "연결"), GUILayout.Height(30))) Connect();
            }

            // ---- 결과 ----
            if (m_Problem != null)
            {
                EditorGUILayout.Space(6);
                EditorGUILayout.HelpBox(m_Problem + "\n" + m_ProblemDetail, MessageType.Error);
            }
            if (m_Done != null)
            {
                EditorGUILayout.Space(6);
                EditorGUILayout.HelpBox(m_Done, MessageType.Info);
            }
            if (connected)
            {
                Title(L("Test cases in the connected sheet", "이어진 시트의 TC"));
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(L("Read the test cases", "TC 불러오기"), GUILayout.Height(24))) LoadCases();
                    SheetKind now = Array.Find(Kinds, kind => kind.Type == connectedType);
                    if (now != null && GUILayout.Button(L("Open the sheet", "시트 열기"), GUILayout.Height(24), GUILayout.Width(110))) now.OpenSheet();
                }
                if (m_Cases != null)
                {
                    Hint(m_Cases.Count == 0
                        ? L("No test cases yet. Write them in the sheet, one per row.", "아직 TC가 없습니다. 시트에 한 줄에 하나씩 적으세요.")
                        : m_Cases.Count + L(" test case(s)", "개의 TC"));
                    foreach (string line in m_Cases) EditorGUILayout.LabelField(line);
                }
                Hint(L("Rules name their test case with a tc: line at the top (tc: TC-012). Run them with qaplay run and the results appear in the sheet.",
                    "규칙 파일의 머리말에 tc: TC-012처럼 TC 번호를 적습니다. qaplay run으로 돌리면 결과가 시트에 나타납니다."));
            }

            EditorGUILayout.EndScrollView();
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

            public override void Load(SheetWindow window)
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

            void InstallKey(SheetWindow window, string file)
            {
                QaplayRunner.Result result = window.Call(L("Checking the key...", "키를 확인하는 중..."), "tc", "key", "--credentials", file, "--json");
                if (result.code != 0)
                {
                    window.Problem(L("That file is not a Google service account key.", "그 파일은 Google 서비스 계정의 키가 아닙니다."),
                        L("Pick the .json file that was downloaded in step 3 (Keys > Add key > Create new key > JSON).",
                            "3번에서 내려받은 .json 파일을 고르세요(키 > 키 추가 > 새 키 만들기 > JSON).") + "\n" + result.error);
                    return;
                }
                window.Forget();
                ReadKey();
            }

            public override bool DrawSource(SheetWindow window)
            {
                Title(L("1. Google key", "1. Google 키"));
                if (m_Email == null)
                {
                    Hint(L("The runner signs in to Google with a key file. Make one in Google Cloud (free), once per PC:",
                        "러너는 키 파일로 Google에 로그인합니다. Google Cloud에서 하나 만듭니다(무료, PC마다 한 번)."));
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
                        if (picked.Length > 0) InstallKey(window, picked);
                    }
                    Hint(L("The key is copied to your user folder (.qaplay), outside the project. It is never put in the project or the settings file.",
                        "키는 사용자 폴더(.qaplay)로 복사됩니다. 프로젝트 밖입니다. 프로젝트나 설정 파일에는 들어가지 않습니다."));
                    return false;
                }

                EditorGUILayout.HelpBox(L("Key ready. The runner signs in as:", "키가 준비됐습니다. 러너는 이 계정으로 로그인합니다:") + "\n" + m_Email, MessageType.Info);
                using (new EditorGUILayout.HorizontalScope())
                {
                    Hint(m_KeyFile);
                    if (GUILayout.Button(L("Use another key...", "다른 키로 바꾸기..."), GUILayout.Width(150)))
                    {
                        string picked = EditorUtility.OpenFilePanel(L("Google key file", "Google 키 파일"), "", "json");
                        if (picked.Length > 0) InstallKey(window, picked);
                    }
                }

                Title(L("2. The sheet", "2. 시트"));
                Step(L("Open your TC sheet in a browser. If you have none, make a new empty one.", "TC 시트를 브라우저로 엽니다. 없으면 빈 시트를 새로 만듭니다."),
                    L("Open: new sheet", "열기: 새 시트 만들기"), NewSheetPage);
                Hint(L("In the sheet press Share, paste this address, choose Editor and press Send:", "시트에서 \"공유\"를 누르고, 이 주소를 붙여 넣고, \"편집자\"를 고른 뒤 \"전송\"을 누릅니다."));
                Copyable(m_Email);
                Hint(L("Then copy the sheet's address from the browser's address bar and paste it here:", "그다음 브라우저의 주소 칸에서 시트의 주소를 복사해 여기에 붙여 넣습니다."));
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
                return m_Address.Trim().Length > 0;
            }

            public override void AddSource(List<string> arguments)
            {
                arguments.Add("--spreadsheet");
                arguments.Add(m_Address.Trim());
            }

            public override void Explain(SheetWindow window, Dictionary<string, object> failure)
            {
                double status = Convert.ToDouble(failure["status"]);
                string message = (string)failure["message"];
                if (status == 404)
                    window.Problem(L("There is no sheet at that address.", "그 주소에 시트가 없습니다."),
                        L("Open the sheet in a browser and copy the whole address from the address bar.", "브라우저로 시트를 열고 주소 칸의 주소를 통째로 복사해 붙여 넣으세요.") + "\n" + message);
                else if (message.Contains("has not been used") || message.Contains("disabled"))
                    window.Problem(L("The Google Sheets API is not enabled for the key's project.", "키의 프로젝트에서 Google Sheets API가 켜져 있지 않습니다."),
                        L("Open the API page (the first button of step 1 when no key is set, or the link in the message), press Enable, wait a minute and check again.",
                            "메시지에 있는 주소를 열어 \"사용\"을 누르고, 1분쯤 뒤에 다시 확인하세요.") + "\n" + message);
                else
                    window.Problem(L("The sheet is not shared with the runner's account yet.", "시트가 아직 러너의 계정에 공유되어 있지 않습니다."),
                        L("In the sheet press Share, paste the address below, choose Editor and press Send. Then check again.",
                            "시트에서 \"공유\"를 누르고, 아래의 주소를 붙여 넣고, \"편집자\"를 고른 뒤 \"전송\"을 누르세요. 그다음 다시 확인하세요.") + "\n" + m_Email);
            }

            public override void OpenSheet()
            {
                if (m_Address.Trim().Length > 0) Application.OpenURL(m_Address.Trim());
            }
        }

        // ---- 종류: 파일(Excel, CSV). 둘은 확장자와 설명만 다르다 ----

        sealed class FileKind : SheetKind
        {
            readonly string m_Type, m_Label, m_Name, m_DefaultFile, m_About;
            string m_File = "";

            public FileKind(string type, string label, string name, string defaultFile, string about)
            {
                m_Type = type;
                m_Label = label;
                m_Name = name;
                m_DefaultFile = defaultFile;
                m_About = about;
            }

            public override string Type => m_Type;
            public override string Label => m_Label;

            public override void Load(SheetWindow window) => m_File = Pref("file." + m_Type);

            void Set(SheetWindow window, string file)
            {
                m_File = file;
                SetPref("file." + m_Type, file);
                window.Forget();
            }

            public override bool DrawSource(SheetWindow window)
            {
                Title("1. " + m_Name);
                Hint(m_About);
                Hint(L("Pick the file your team already uses, or make a new one. A new file is created with the column names when you connect.",
                    "팀이 쓰던 파일을 고르거나 새로 만듭니다. 새 파일은 연결할 때 칸 이름과 함께 만들어집니다."));
                using (new EditorGUILayout.HorizontalScope())
                {
                    string file = EditorGUILayout.TextField(m_File);
                    if (file != m_File) Set(window, file);
                    if (GUILayout.Button(L("Pick...", "고르기..."), GUILayout.Width(70)))
                    {
                        string picked = EditorUtility.OpenFilePanel(m_Name, ProjectFolder, m_Type);
                        if (picked.Length > 0) Set(window, Relative(picked));
                        GUI.FocusControl(null);
                    }
                    if (GUILayout.Button(L("New...", "새로 만들기..."), GUILayout.Width(100)))
                    {
                        string picked = EditorUtility.SaveFilePanel(m_Name, ProjectFolder, m_DefaultFile, m_Type);
                        if (picked.Length > 0) Set(window, Relative(picked));
                        GUI.FocusControl(null);
                    }
                }
                Hint(L("Keep the file where the whole team can reach it: in the project (then it is shared through Git) or on a shared drive.",
                    "팀이 함께 볼 수 있는 곳에 둡니다: 프로젝트 안(Git으로 공유됩니다)이나 공유 드라이브."));
                return m_File.Trim().Length > 0;
            }

            public override void AddSource(List<string> arguments)
            {
                arguments.Add("--file");
                arguments.Add(m_File.Trim());
            }

            public override void Explain(SheetWindow window, Dictionary<string, object> failure) =>
                window.Problem(L("The file could not be opened.", "파일을 열지 못했습니다."), (string)failure["message"]);

            public override void OpenSheet()
            {
                string file = Path.GetFullPath(Path.Combine(ProjectFolder, m_File.Trim()));
                if (File.Exists(file)) EditorUtility.OpenWithDefaultApp(file);
            }
        }
    }
}
