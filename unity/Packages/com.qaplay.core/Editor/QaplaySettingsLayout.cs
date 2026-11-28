using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using static Qaplay.Editor.QaplayUi;

namespace Qaplay.Editor
{
    // 설정 화면의 "배치 검사의 예외" 부분. 의도한 겹침과 넘침을 사유와 함께 적어 두면 배치 검사(layout, 규칙의 assertLayout)에서 빠진다.
    // 목록은 설정의 exceptions 파일(기본 qaplay-layout-exceptions.json)이고, 읽고 쓰는 일은 러너(qaplay exceptions)가 한다
    sealed partial class QaplaySettingsProvider
    {
        List<Dictionary<string, object>> m_Exceptions;
        string m_ExceptionsFile, m_LayoutNote;
        List<Dictionary<string, object>> m_Issues;
        readonly Dictionary<string, string> m_Reasons = new Dictionary<string, string>();
        bool m_Finding;

        void LoadExceptions()
        {
            m_Exceptions = null;
            if (QaplayRunner.Find() == null) return;
            QaplayRunner.Result result = QaplayRunner.Run(ProjectFolder, "exceptions", "list", "--json");
            if (result.json == null)
            {
                m_LayoutNote = L("The exception list could not be read: ", "예외 목록을 읽지 못했습니다: ") + result.error;
                return;
            }
            m_ExceptionsFile = result.json["file"] as string;
            m_Exceptions = ((List<object>)result.json["exceptions"]).ConvertAll(entry => (Dictionary<string, object>)entry);
        }

        // 지금 게임 화면의 배치 문제(이미 예외인 것은 빠진다). 게임에 닿으므로 뒤에서 부른다
        void FindIssues()
        {
            m_Finding = true;
            m_LayoutNote = null;
            QaplayRunner.RunLater(ProjectFolder, result =>
            {
                m_Finding = false;
                if (result.json == null)
                {
                    m_Issues = null;
                    m_LayoutNote = L("The game could not be checked. Press Play first (or start the game where the settings say it runs).\n",
                        "게임을 검사하지 못했습니다. 먼저 Play를 누르세요(또는 설정의 실행 대상에서 게임을 띄우세요).\n") + result.error;
                    return;
                }
                m_Issues = ((List<object>)result.json["issues"]).ConvertAll(entry => (Dictionary<string, object>)entry);
                int ignored = result.json.TryGetValue("ignored", out object value) && value is double number ? (int)number : 0;
                m_LayoutNote = (m_Issues.Count == 0 ? L("No layout issues on this screen.", "이 화면에는 배치 문제가 없습니다.") : m_Issues.Count + L(" issue(s) on this screen.", "개의 문제가 이 화면에 있습니다."))
                               + (ignored > 0 ? " " + ignored + L(" more are exceptions already.", "개는 이미 예외입니다.") : "");
            }, "layout", "--json");
        }

        static string Key(Dictionary<string, object> issue) => issue["kind"] + "\n" + issue["path"] + "\n" + (issue.TryGetValue("otherPath", out object other) ? other : "");

        void AddException(Dictionary<string, object> issue, string reason)
        {
            var arguments = new List<string> { "exceptions", "add", "--issue-kind", (string)issue["kind"], "--issue-path", (string)issue["path"], "--reason", reason };
            if (issue.TryGetValue("otherPath", out object other) && other is string path) arguments.AddRange(new[] { "--issue-other-path", path });
            QaplayRunner.Result result = Call(L("Adding the exception...", "예외를 등록하는 중..."), arguments.ToArray());
            if (result.code != 0)
            {
                m_LayoutNote = result.error;
                return;
            }
            m_Issues.Remove(issue);
            LoadExceptions();
        }

        void DrawLayoutExceptions()
        {
            Title(L("Layout check: intended issues", "배치 검사: 의도한 문제(예외)"));
            Hint(L("The layout check (qaplay layout, assertLayout in rules) reports text that does not fit and elements that overlap. "
                   + "Register the ones that are on purpose, with why: they are skipped from then on. The list is a file in the project; put it in version control.",
                "배치 검사(qaplay layout, 규칙의 assertLayout)는 넘친 글자와 겹친 요소를 알립니다. 일부러 그렇게 한 것은 사유와 함께 등록하면 그 뒤로 빠집니다. "
                + "목록은 프로젝트의 파일입니다. 저장소에 넣으세요."));
            if (m_ExceptionsFile != null) EditorGUILayout.SelectableLabel(m_ExceptionsFile, EditorStyles.miniLabel, GUILayout.Height(16));

            // ---- 등록된 것 ----
            if (m_Exceptions != null)
            {
                if (m_Exceptions.Count == 0) Hint(L("(no exceptions yet)", "(아직 예외가 없습니다)"));
                for (int n = 0; n < m_Exceptions.Count; n++)
                {
                    Dictionary<string, object> entry = m_Exceptions[n];
                    string number = (n + 1).ToString();
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField(entry["kind"] + ": " + entry["path"] + (entry.TryGetValue("otherPath", out object other) ? " ~ " + other : ""), GUILayout.MinWidth(200));
                        string reason = entry.TryGetValue("reason", out object r) ? r as string ?? "" : "";
                        string now = EditorGUILayout.DelayedTextField(reason, GUILayout.MinWidth(160));
                        if (now != reason && now.Trim().Length > 0)
                        {
                            Call(L("Saving...", "저장하는 중..."), "exceptions", "set", number, "--reason", now.Trim());
                            LoadExceptions();
                            GUIUtility.ExitGUI();
                        }
                        if (GUILayout.Button(L("Remove", "빼기"), GUILayout.Width(70)))
                        {
                            Call(L("Removing...", "빼는 중..."), "exceptions", "delete", number);
                            LoadExceptions();
                            GUIUtility.ExitGUI();
                        }
                    }
                }
            }

            // ---- 지금 화면에서 찾기 ----
            EditorGUILayout.Space(2);
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(m_Finding))
                    if (GUILayout.Button(m_Finding ? L("Checking the screen...", "화면을 검사하는 중...") : L("Find the issues on the current game screen", "지금 게임 화면의 문제 찾기"), GUILayout.Height(24)))
                        FindIssues();
                if (!EditorApplication.isPlaying) GUILayout.Label(L("(press Play first)", "(먼저 Play)"), EditorStyles.miniLabel, GUILayout.ExpandWidth(false));
            }
            if (m_LayoutNote != null) Hint(m_LayoutNote);
            if (m_Issues == null) return;
            foreach (Dictionary<string, object> issue in m_Issues.ToArray())
            {
                string key = Key(issue);
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(issue["kind"] + ": " + (issue.TryGetValue("description", out object description) ? description : issue["path"]), EditorStyles.wordWrappedLabel, GUILayout.MinWidth(200));
                    m_Reasons.TryGetValue(key, out string reason);
                    m_Reasons[key] = EditorGUILayout.TextField(reason ?? "", GUILayout.MinWidth(160));
                    using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(m_Reasons[key])))
                        if (GUILayout.Button(L("It is intended", "의도한 것"), GUILayout.Width(90)))
                        {
                            AddException(issue, m_Reasons[key].Trim());
                            GUIUtility.ExitGUI();
                        }
                }
            }
            if (m_Issues.Count > 0) Hint(L("Write why in the box (for example \"the popup covers the menu on purpose\"), then press It is intended.",
                "칸에 사유를 적고(예: \"팝업이 메뉴를 일부러 덮는다\") \"의도한 것\"을 누르세요."));
        }
    }
}
