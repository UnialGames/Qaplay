using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Qaplay.Editor
{
    /// <summary>
    /// Google Sheets를 잇는 창(qaplay > Google Sheet). 위에서 아래로 따라가면 끝난다: 키 파일을 고르고, 시트의 주소를 붙여 넣고,
    /// 탭과 칸을 확인하고, "연결"을 누른다. Google Cloud에서 키를 받는 방법은 창 안에 단계별로 적혀 있고, 단계마다 그 페이지를 여는 버튼이 있다.
    /// Google과 이야기하는 일은 러너가 한다(QaplayRunner). 이 창은 러너를 부르고 결과를 보여 준다.
    /// </summary>
    sealed class SheetWindow : EditorWindow
    {
        const string ApiPage = "https://console.cloud.google.com/apis/library/sheets.googleapis.com";
        const string CreateAccountPage = "https://console.cloud.google.com/iam-admin/serviceaccounts/create";
        const string AccountsPage = "https://console.cloud.google.com/iam-admin/serviceaccounts";
        const string NewSheetPage = "https://sheets.new";
        const string AddressKey = "qaplay.sheet.address";

        // 화면의 말은 PC의 언어를 따른다: 한국어면 한국어, 아니면 영어
        static readonly bool Korean = Application.systemLanguage == SystemLanguage.Korean;
        static string L(string english, string korean) => Korean ? korean : english;

        sealed class Tab
        {
            public string title;
            public List<string> header = new List<string>();
            public bool korean;
            // 고른 칸: header의 번호. -1은 "없음"
            public int tc = -1, content = -1, expected = -1;
        }

        string m_Email, m_KeyFile;
        string m_Address = "";
        string m_Problem, m_ProblemDetail;
        List<Tab> m_Tabs;
        int m_Tab, m_Language;
        string m_Rules = "";
        string m_Done;
        List<string> m_Cases;
        Vector2 m_Page;

        static string ProjectFolder => Directory.GetParent(Application.dataPath).FullName;
        static string SettingsFile => Path.Combine(ProjectFolder, "qaplay-sheet.json");

        [MenuItem("qaplay/Google Sheet")]
        static void Open()
        {
            var window = GetWindow<SheetWindow>("qaplay Google Sheet");
            window.minSize = new Vector2(460, 420);
        }

        void OnEnable()
        {
            m_Address = EditorPrefs.GetString(AddressKey + "." + ProjectFolder, "");
            m_Language = Korean ? 1 : 0;
            m_Rules = GuessRules();
            ReadKey();
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

        static string Relative(string path) => Path.GetRelativePath(ProjectFolder, path).Replace('\\', '/');

        // ---- 러너 부르기 ----

        QaplayRunner.Result Call(string busy, params string[] arguments)
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

        void ReadKey()
        {
            m_Email = null;
            if (QaplayRunner.Find() == null) return;
            QaplayRunner.Result result = QaplayRunner.Run(ProjectFolder, "tc", "key", "--json");
            if (result.json == null) return;
            m_Email = result.json["email"] as string;
            m_KeyFile = result.json["file"] as string;
        }

        void InstallKey(string file)
        {
            QaplayRunner.Result result = Call(L("Checking the key...", "키를 확인하는 중..."), "tc", "key", "--credentials", file, "--json");
            if (result.code != 0)
            {
                Problem(L("That file is not a Google service account key.", "그 파일은 Google 서비스 계정의 키가 아닙니다."),
                    L("Pick the .json file that was downloaded in step 3 (Keys > Add key > Create new key > JSON).",
                        "3번에서 내려받은 .json 파일을 고르세요(키 > 키 추가 > 새 키 만들기 > JSON).") + "\n" + result.error);
                return;
            }
            m_Problem = null;
            ReadKey();
        }

        void Probe()
        {
            m_Tabs = null;
            m_Done = null;
            m_Cases = null;
            EditorPrefs.SetString(AddressKey + "." + ProjectFolder, m_Address);
            QaplayRunner.Result result = Call(L("Opening the sheet...", "시트를 여는 중..."), "tc", "tabs", "--spreadsheet", m_Address.Trim(), "--json");
            if (result.json == null)
            {
                Problem(L("The sheet could not be opened.", "시트를 열지 못했습니다."), result.error);
                return;
            }
            if (!true.Equals(result.json["ok"]))
            {
                double status = Convert.ToDouble(result.json["status"]);
                string message = (string)result.json["message"];
                if (status == 404)
                    Problem(L("There is no sheet at that address.", "그 주소에 시트가 없습니다."),
                        L("Open the sheet in a browser and copy the whole address from the address bar.", "브라우저로 시트를 열고 주소 칸의 주소를 통째로 복사해 붙여 넣으세요.") + "\n" + message);
                else if (message.Contains("has not been used") || message.Contains("disabled"))
                    Problem(L("The Google Sheets API is not enabled for the key's project.", "키의 프로젝트에서 Google Sheets API가 켜져 있지 않습니다."),
                        L("Press \"1. Open: enable the Sheets API\" above, press Enable there, wait a minute and check again.",
                            "위의 \"1. 열기: Sheets API 켜기\"를 눌러 그 페이지에서 \"사용\"을 누르고, 1분쯤 뒤에 다시 확인하세요.") + "\n" + message);
                else
                    Problem(L("The sheet is not shared with the runner's account yet.", "시트가 아직 러너의 계정에 공유되어 있지 않습니다."),
                        L("In the sheet press Share, paste the address below, choose Editor and press Send. Then check again.",
                            "시트에서 \"공유\"를 누르고, 아래의 주소를 붙여 넣고, \"편집자\"를 고른 뒤 \"전송\"을 누르세요. 그다음 다시 확인하세요.") + "\n" + m_Email);
                return;
            }
            m_Problem = null;
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
            // TC 번호로 보이는 칸이 있는 첫 탭을 고른다
            m_Tab = Mathf.Max(0, m_Tabs.FindIndex(tab => tab.tc >= 0));
            if (m_Tabs.Count > 0 && m_Tabs[m_Tab].korean) m_Language = 1;
        }

        void Connect()
        {
            Tab tab = m_Tabs[m_Tab];
            // 새 설정은 옆의 파일에 먼저 쓴다. 잇는 데 실패하면 쓰던 설정이 그대로 남는다
            string fresh = SettingsFile + ".new";
            if (File.Exists(fresh)) File.Delete(fresh);
            var arguments = new List<string>
            {
                "tc", "init", "--sheet", fresh, "--spreadsheet", m_Address.Trim(), "--tab", tab.title, "--lang", m_Language == 1 ? "ko" : "en",
            };
            if (m_Rules.Trim().Length > 0)
            {
                arguments.Add("--rules");
                arguments.Add(m_Rules.Trim());
            }
            void Column(string option, int index)
            {
                if (index < 0) return;
                arguments.Add(option);
                arguments.Add(tab.header[index]);
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

        void Problem(string what, string detail)
        {
            m_Problem = what;
            m_ProblemDetail = detail;
        }

        // ---- 화면 ----

        // 긴 글은 창의 폭에 맞춰 여러 줄로 꺾인다
        static void Hint(string text) => GUILayout.Label(text, EditorStyles.wordWrappedMiniLabel);

        static void Title(string text)
        {
            EditorGUILayout.Space(10);
            GUILayout.Label(text, EditorStyles.boldLabel);
        }

        // 한 줄: 설명과, 그 페이지를 여는 버튼
        static void Step(string text, string button, string url)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(text, EditorStyles.wordWrappedLabel);
                if (GUILayout.Button(button, GUILayout.Width(150))) Application.OpenURL(url);
            }
        }

        static void Copyable(string text)
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

            string runner = QaplayRunner.Find();
            if (runner == null)
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
                        ReadKey();
                    }
                }
                EditorGUILayout.EndScrollView();
                return;
            }

            bool connected = File.Exists(SettingsFile);
            if (connected)
                EditorGUILayout.HelpBox(L("This project is connected to a sheet. qaplay run writes its results there when it is run in:\n",
                    "이 프로젝트는 시트에 이어져 있습니다. 아래 폴더에서 qaplay run을 돌리면 결과가 시트에 적힙니다.\n") + ProjectFolder, MessageType.Info);
            else
                Hint(L("Follow the steps from top to bottom. It takes about five minutes the first time.", "위에서 아래로 차례대로 하면 됩니다. 처음 한 번은 5분쯤 걸립니다."));

            // ---- 1. 키 ----
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
                    if (picked.Length > 0) InstallKey(picked);
                }
                Hint(L("The key is copied to your user folder (.qaplay), outside the project. It is never put in the project or the settings file.",
                    "키는 사용자 폴더(.qaplay)로 복사됩니다. 프로젝트 밖입니다. 프로젝트나 설정 파일에는 들어가지 않습니다."));
            }
            else
            {
                EditorGUILayout.HelpBox(L("Key ready. The runner signs in as:", "키가 준비됐습니다. 러너는 이 계정으로 로그인합니다:") + "\n" + m_Email, MessageType.Info);
                using (new EditorGUILayout.HorizontalScope())
                {
                    Hint(m_KeyFile);
                    if (GUILayout.Button(L("Use another key...", "다른 키로 바꾸기..."), GUILayout.Width(150)))
                    {
                        string picked = EditorUtility.OpenFilePanel(L("Google key file", "Google 키 파일"), "", "json");
                        if (picked.Length > 0) InstallKey(picked);
                    }
                }
            }

            // ---- 2. 시트 ----
            using (new EditorGUI.DisabledScope(m_Email == null))
            {
                Title(L("2. The sheet", "2. 시트"));
                Step(L("Open your TC sheet in a browser. If you have none, make a new empty one.", "TC 시트를 브라우저로 엽니다. 없으면 빈 시트를 새로 만듭니다."),
                    L("Open: new sheet", "열기: 새 시트 만들기"), NewSheetPage);
                if (m_Email != null)
                {
                    Hint(L("In the sheet press Share, paste this address, choose Editor and press Send:", "시트에서 \"공유\"를 누르고, 이 주소를 붙여 넣고, \"편집자\"를 고른 뒤 \"전송\"을 누릅니다."));
                    Copyable(m_Email);
                }
                Hint(L("Then copy the sheet's address from the browser's address bar and paste it here:", "그다음 브라우저의 주소 칸에서 시트의 주소를 복사해 여기에 붙여 넣습니다."));
                using (new EditorGUILayout.HorizontalScope())
                {
                    m_Address = EditorGUILayout.TextField(m_Address);
                    if (GUILayout.Button(L("Paste", "붙여넣기"), GUILayout.Width(70)))
                    {
                        m_Address = EditorGUIUtility.systemCopyBuffer.Trim();
                        GUI.FocusControl(null);
                    }
                }
                using (new EditorGUI.DisabledScope(m_Address.Trim().Length == 0))
                    if (GUILayout.Button(L("Check the connection", "연결 확인"), GUILayout.Height(26))) Probe();
            }

            // ---- 3. 탭과 칸 ----
            if (m_Tabs != null && m_Tabs.Count > 0)
            {
                Title(L("3. Tab and columns", "3. 탭과 칸"));
                EditorGUILayout.HelpBox(L("The sheet opened. Check what was found, then press Connect.", "시트가 열렸습니다. 찾은 내용을 확인하고 \"연결\"을 누르세요."), MessageType.Info);
                m_Tab = EditorGUILayout.Popup(L("Tab with the test cases", "TC가 있는 탭"), m_Tab, m_Tabs.ConvertAll(tab => tab.title).ToArray());
                Tab tab = m_Tabs[m_Tab];
                if (tab.header.Count == 0)
                    Hint(L("This tab is empty. The column names (TC, Steps, Expected) will be written for you; then write your test cases under them.",
                        "빈 탭입니다. 칸 이름(TC 번호, 내용, 기대 결과)을 적어 드립니다. 그 아래에 TC를 적으면 됩니다."));
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
                    if (GUILayout.Button(connected ? L("Connect again", "다시 연결") : L("Connect", "연결"), GUILayout.Height(30))) Connect();
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
                Title(L("Test cases in the sheet", "시트의 TC"));
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(L("Read the test cases", "TC 불러오기"), GUILayout.Height(24))) LoadCases();
                    if (m_Address.Trim().Length > 0 && GUILayout.Button(L("Open the sheet", "시트 열기"), GUILayout.Height(24), GUILayout.Width(110)))
                        Application.OpenURL(m_Address.Trim());
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
    }
}
