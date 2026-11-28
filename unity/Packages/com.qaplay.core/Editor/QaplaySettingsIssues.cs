using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using static Qaplay.Editor.QaplayUi;

namespace Qaplay.Editor
{
    /// <summary>
    /// 설정 화면의 "버그를 올릴 곳" 부분. 버그 리포트는 늘 이 PC의 폴더에 있고, 이슈 관리 도구를 이으면 거기에도 올라간다(설정의 issues).
    /// 도구의 종류(사용하지 않음, Jira)를 고르고 위에서 아래로 따라간다. 도구와 이야기하는 일은 러너(qaplay bugs ...)가 한다.
    /// </summary>
    sealed class IssueSetupView
    {
        /// <summary>도구의 종류 하나가 이 화면에서 맡는 부분. 종류를 더하려면 이것을 하나 더 만들어 Kinds에 넣는다(러너에는 같은 이름의 도구가 있어야 한다).</summary>
        internal abstract class IssueKind
        {
            public abstract string Type { get; }
            public abstract string Label { get; }
            public abstract void Load(Dictionary<string, object> issues);
            /// <summary>이어지지 않았을 때 따라가는 단계들.</summary>
            public abstract void Draw(IssueSetupView view);
            /// <summary>이어져 있을 때 한 줄 설명과 그 도구에서 여는 주소.</summary>
            public abstract string Describe(Dictionary<string, object> issues, out string url);
        }

        static IssueKind[] s_Kinds;
        static IssueKind[] Kinds => s_Kinds ??= new IssueKind[] { new JiraKind() };

        int m_Kind; // 0: 사용하지 않음, 1부터 Kinds
        // 이어진 상태에서 "다른 곳으로 바꾸기"를 눌렀다
        internal bool m_Changing;
        List<string> m_Installed;
        internal string m_Problem, m_Done;

        public void OnEnable(Dictionary<string, object> settings)
        {
            m_Installed = null;
            m_Problem = m_Done = null;
            if (QaplayRunner.Find() != null)
            {
                QaplayRunner.Result result = QaplayRunner.Run(ProjectFolder, "modules", "--json");
                if (result.json != null && result.json.TryGetValue("issues", out object value) && value is List<object> types)
                    m_Installed = types.ConvertAll(type => type as string);
            }
            Dictionary<string, object> issues = Issues(settings);
            string type = issues?.GetValueOrDefault("type") as string;
            m_Kind = type == null ? 0 : Array.FindIndex(Kinds, kind => kind.Type == type) + 1;
            foreach (IssueKind kind in Kinds) kind.Load(issues);
        }

        static Dictionary<string, object> Issues(Dictionary<string, object> settings) =>
            settings != null && settings.TryGetValue("issues", out object value) ? value as Dictionary<string, object> : null;

        // 러너가 알린 까닭. Jira의 거절은 할 일(hint)이 이미 괄호로 붙어 있다
        internal static string Failure(QaplayRunner.Result result) =>
            result.json != null && result.json.TryGetValue("message", out object message) ? message as string
            : result.error.Length > 0 ? result.error : result.output;

        public void OnGUI(Dictionary<string, object> settings, Action<Action<Dictionary<string, object>>> change)
        {
            Dictionary<string, object> issues = Issues(settings);
            string connectedType = issues?.GetValueOrDefault("type") as string;
            IssueKind connected = connectedType == null ? null : Array.Find(Kinds, kind => kind.Type == connectedType);
            // 이어진 상태: 어디로 가는지와 할 수 있는 일만. 바꾸는 중일 때만 아래의 단계를 보인다
            if (connected != null && !m_Changing)
            {
                string line = connected.Describe(issues, out string url);
                EditorGUILayout.HelpBox(L("Connected. ", "연결되어 있습니다. ") + line + "\n"
                                        + L("Bugs are sent when qaplay run registers them, after bugs pull and verify, and with Send now. Their status comes back from there. Bug reports also stay on this PC.",
                                            "qaplay run이 버그를 올릴 때, bugs pull과 verify 뒤에, 그리고 \"지금 보내기\"로 보냅니다. 상태는 그쪽에서 받아 옵니다. 버그 리포트는 이 PC에도 남습니다."), MessageType.Info);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(L("Send now", "지금 보내기"), GUILayout.Height(24)))
                    {
                        QaplayRunner.Result result = Call(L("Sending the bugs...", "버그를 보내는 중..."), "bugs", "sync");
                        m_Done = result.code == 0 ? (result.output.Length == 0 ? L("Nothing new to send.", "보낼 것이 없습니다.") : result.output) : null;
                        m_Problem = result.code == 0 ? null : result.output + "\n" + result.error;
                    }
                    if (url != null && GUILayout.Button(L("Open it", "열기"), GUILayout.Height(24))) Application.OpenURL(url);
                    if (GUILayout.Button(L("Change...", "다른 곳으로 바꾸기..."), GUILayout.Height(24)))
                    {
                        m_Changing = true;
                        m_Problem = m_Done = null;
                    }
                    if (GUILayout.Button(L("Disconnect...", "연결 끊기..."), GUILayout.Height(24))
                        && EditorUtility.DisplayDialog("qaplay", L("Stop sending bugs there? The bugs stay on this PC, and the issues already made stay in the tracker.",
                            "더는 거기로 버그를 보내지 않을까요? 버그는 이 PC에 남고, 이미 만든 이슈는 그쪽에 남습니다."), L("Disconnect", "끊기"), L("Cancel", "취소")))
                    {
                        change(json => json.Remove("issues"));
                        m_Kind = 0;
                        m_Problem = m_Done = null;
                        GUIUtility.ExitGUI();
                    }
                }
                if (m_Problem != null) EditorGUILayout.HelpBox(m_Problem.Trim(), MessageType.Error);
                if (m_Done != null) EditorGUILayout.HelpBox(m_Done.Trim(), MessageType.Info);
                return;
            }
            if (connected != null)
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.HelpBox(L("Changing where bugs go. The current one stays until you press Connect at the end.",
                        "버그를 올릴 곳을 바꾸는 중입니다. 끝에서 \"연결\"을 누르기 전까지는 지금 것이 그대로입니다."), MessageType.None);
                    if (GUILayout.Button(L("Cancel", "취소"), GUILayout.Width(80), GUILayout.Height(38)))
                    {
                        m_Changing = false;
                        m_Problem = m_Done = null;
                        GUIUtility.ExitGUI();
                    }
                }
            else
                EditorGUILayout.HelpBox(L("Not connected: bug reports are kept on this PC only (the folder above). Choose an issue tracker to send them there too.",
                    "잇지 않았습니다: 버그 리포트는 이 PC(위의 폴더)에만 남습니다. 이슈 관리 도구를 고르면 거기에도 올라갑니다."), MessageType.None);

            var labels = new List<string> { L("Not used", "사용하지 않음") };
            foreach (IssueKind kind in Kinds) labels.Add(kind.Label);
            int picked = GUILayout.Toolbar(m_Kind, labels.ToArray(), GUILayout.Height(24));
            if (picked != m_Kind)
            {
                m_Kind = picked;
                m_Problem = m_Done = null;
                GUI.FocusControl(null);
            }
            if (m_Kind > 0)
            {
                IssueKind kind = Kinds[m_Kind - 1];
                if (m_Installed != null && !m_Installed.Contains(kind.Type))
                    EditorGUILayout.HelpBox(kind.Label + L(" comes as a module of the runner (modules/" + kind.Type + "), and the runner on this PC does not have it. "
                                                           + "The runner comes with its modules folder; when it is built from the qaplay repository, the modules are built with it.",
                        "은(는) 러너의 모듈(modules/" + kind.Type + ")로 따로 들어가는데, 이 PC의 러너에는 없습니다. 러너를 받을 때 modules 폴더가 함께 옵니다. qaplay 저장소에서 빌드했다면 함께 만들어집니다."),
                        MessageType.Warning);
                else kind.Draw(this);
            }

            if (m_Problem != null) EditorGUILayout.HelpBox(m_Problem.Trim(), MessageType.Error);
            if (m_Done != null) EditorGUILayout.HelpBox(m_Done.Trim(), MessageType.Info);
        }

        // ---- 종류: Jira ----

        sealed class JiraKind : IssueKind
        {
            const string TokenPage = "https://id.atlassian.com/manage-profile/security/api-tokens";

            string m_Site = "", m_Email = "", m_Token = "", m_LoggedIn;
            bool m_Change;
            List<(string key, string name, List<string> types)> m_Projects;
            int m_Project, m_Type;

            public override string Type => "jira";
            public override string Label => "Jira";

            static string Pref(string name) => EditorPrefs.GetString("qaplay.jira." + name + "." + ProjectFolder, "");
            static void SetPref(string name, string value) => EditorPrefs.SetString("qaplay.jira." + name + "." + ProjectFolder, value);

            public override void Load(Dictionary<string, object> issues)
            {
                m_Site = issues?.GetValueOrDefault("site") as string ?? Pref("site");
                m_Email = Pref("email");
                m_Token = "";
                m_Projects = null;
                ReadLogin();
            }

            // 이 PC가 그 사이트에 누구로 로그인하는지(토큰은 러너가 사용자 폴더에 둔다. 이 화면은 보지 않는다)
            void ReadLogin()
            {
                m_LoggedIn = null;
                if (m_Site.Trim().Length == 0 || QaplayRunner.Find() == null) return;
                QaplayRunner.Result result = QaplayRunner.Run(ProjectFolder, "bugs", "jira", "--site", m_Site.Trim(), "--json");
                if (result.json != null) m_LoggedIn = result.json.GetValueOrDefault("email") as string;
                if (m_LoggedIn != null && m_Email.Length == 0) m_Email = m_LoggedIn;
            }

            public override string Describe(Dictionary<string, object> issues, out string url)
            {
                string site = issues.GetValueOrDefault("site") as string, project = issues.GetValueOrDefault("project") as string;
                url = site == null || project == null ? null : site.TrimEnd('/') + "/browse/" + project;
                return L("Bugs go to Jira: ", "버그가 Jira로 올라갑니다: ") + site + "  " + L("project ", "프로젝트 ") + project + "  (" + (issues.GetValueOrDefault("issueType") ?? "Bug") + ")";
            }

            public override void Draw(IssueSetupView view)
            {
                Hint(L("qaplay signs in to Jira Cloud with your Atlassian account's email and an API token. The token stays on this PC (your user folder), never in the project.",
                    "qaplay는 Atlassian 계정의 이메일과 API 토큰으로 Jira Cloud에 로그인합니다. 토큰은 이 PC(사용자 폴더)에만 두고 프로젝트에는 넣지 않습니다."));

                Title(L("1. Jira site", "1. Jira 사이트"));
                string site = EditorGUILayout.DelayedTextField(L("Site address", "사이트 주소"), m_Site);
                if (site != m_Site)
                {
                    m_Site = site.Trim();
                    SetPref("site", m_Site);
                    m_Projects = null;
                    ReadLogin();
                }
                Hint(L("The address you open Jira with, for example your-team.atlassian.net.", "Jira를 열 때의 주소입니다. 예: your-team.atlassian.net"));
                if (m_Site.Length == 0) return;

                Title(L("2. Log in", "2. 로그인"));
                if (m_LoggedIn != null && !m_Change)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField(L("This PC logs in as ", "이 PC는 이 계정으로 로그인합니다: ") + m_LoggedIn);
                        if (GUILayout.Button(L("Another account...", "다른 계정으로..."), GUILayout.Width(140))) m_Change = true;
                    }
                }
                else
                {
                    m_Email = EditorGUILayout.TextField(L("Account email", "계정 이메일"), m_Email);
                    Step(L("Make an API token: press \"Create API token\" (not the one with scopes), name it qaplay, pick when it expires, then Copy.",
                            "API 토큰을 만듭니다: \"Create API token\"(범위가 있는 것이 아닌 것)을 누르고, 이름을 qaplay로, 만료일을 고른 뒤 복사합니다."),
                        L("Open: API tokens", "열기: API 토큰"), TokenPage);
                    m_Token = EditorGUILayout.PasswordField(L("API token (paste)", "API 토큰(붙여 넣기)"), m_Token);
                    using (new EditorGUI.DisabledScope(m_Email.Trim().Length == 0 || m_Token.Trim().Length == 0))
                        if (GUILayout.Button(L("Log in", "로그인"), GUILayout.Height(26))) LogIn(view);
                    Hint(L("When the token expires, make a new one and log in again here.", "토큰이 만료되면 새로 만들어 여기서 다시 로그인합니다."));
                    if (m_LoggedIn == null) return;
                }

                Title(L("3. Project", "3. 프로젝트"));
                if (m_Projects == null)
                {
                    if (GUILayout.Button(L("Read the projects", "프로젝트 불러오기"), GUILayout.Height(24))) ReadProjects(view);
                    return;
                }
                if (m_Projects.Count == 0)
                {
                    EditorGUILayout.HelpBox(L("This account sees no project on that site. Ask a Jira admin to add it to the QA project.",
                        "이 계정이 그 사이트에서 보는 프로젝트가 없습니다. Jira 관리자에게 QA 프로젝트에 넣어 달라고 하세요."), MessageType.Warning);
                    return;
                }
                int project = EditorGUILayout.Popup(L("Project", "프로젝트"), m_Project, m_Projects.ConvertAll(p => p.key + "  " + p.name).ToArray());
                if (project != m_Project)
                {
                    m_Project = project;
                    m_Type = Mathf.Max(0, m_Projects[project].types.IndexOf("Bug"));
                }
                List<string> types = m_Projects[m_Project].types;
                if (types.Count > 0) m_Type = EditorGUILayout.Popup(L("Issue type", "이슈 유형"), Mathf.Min(m_Type, types.Count - 1), types.ToArray());
                Hint(L("Each bug becomes an issue of this type, with the screen, the log and the steps attached and the label qaplay.",
                    "버그마다 이 유형의 이슈가 하나 생깁니다. 화면, 로그, 재현 스텝이 첨부되고 qaplay 라벨이 붙습니다."));
                using (new EditorGUI.DisabledScope(types.Count == 0))
                    if (GUILayout.Button(L("Connect", "연결"), GUILayout.Height(30))) Connect(view, types[m_Type]);
            }

            void LogIn(IssueSetupView view)
            {
                SetPref("email", m_Email.Trim());
                QaplayRunner.Result result;
                EditorUtility.DisplayProgressBar("qaplay", L("Logging in to Jira...", "Jira에 로그인하는 중..."), 0.5f);
                try
                {
                    result = QaplayRunner.RunWithInput(ProjectFolder, m_Token.Trim(), "bugs", "jira", "login", "--site", m_Site, "--email", m_Email.Trim(), "--json");
                }
                finally
                {
                    EditorUtility.ClearProgressBar();
                }
                m_Token = "";
                GUI.FocusControl(null);
                if (result.code != 0)
                {
                    view.m_Problem = L("Could not log in: ", "로그인하지 못했습니다: ") + Failure(result);
                    return;
                }
                view.m_Problem = null;
                view.m_Done = L("Logged in as ", "로그인했습니다: ") + result.json?.GetValueOrDefault("name");
                m_Change = false;
                ReadLogin();
            }

            void ReadProjects(IssueSetupView view)
            {
                QaplayRunner.Result result = Call(L("Reading the projects...", "프로젝트를 불러오는 중..."), "bugs", "jira", "projects", "--site", m_Site, "--json");
                if (result.code != 0 || result.json == null)
                {
                    view.m_Problem = L("Could not read the projects: ", "프로젝트를 불러오지 못했습니다: ") + Failure(result);
                    return;
                }
                view.m_Problem = null;
                m_Projects = new List<(string, string, List<string>)>();
                foreach (object entry in (List<object>)result.json["projects"])
                {
                    var data = (Dictionary<string, object>)entry;
                    m_Projects.Add(((string)data["key"], (string)data["name"], ((List<object>)data["issueTypes"]).ConvertAll(t => (string)t)));
                }
                m_Project = 0;
                m_Type = m_Projects.Count == 0 ? 0 : Mathf.Max(0, m_Projects[0].types.IndexOf("Bug"));
            }

            void Connect(IssueSetupView view, string type)
            {
                QaplayRunner.Result result = Call(L("Connecting...", "잇는 중..."), "bugs", "jira", "connect", "--site", m_Site, "--project", m_Projects[m_Project].key,
                    "--issue-type", type, "--settings", SettingsFile, "--json");
                if (result.code != 0)
                {
                    view.m_Problem = L("Could not connect: ", "잇지 못했습니다: ") + Failure(result);
                    return;
                }
                view.m_Problem = null;
                view.m_Changing = false;
                view.m_Done = L("Just connected. Bugs already on this PC are sent with Send now.", "방금 연결했습니다. 이 PC에 이미 있는 버그는 \"지금 보내기\"로 보냅니다.");
            }
        }
    }
}
