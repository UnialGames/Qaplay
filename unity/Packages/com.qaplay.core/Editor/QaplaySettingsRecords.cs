using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using static Qaplay.Editor.QaplayUi;

namespace Qaplay.Editor
{
    // 설정 화면의 "리포트를 얼마나 둘지" 부분: 설정 파일의 retention. 러너가 실행할 때마다(그리고 qaplay reports clean이) 이 기한을 넘은 것을 지운다
    sealed partial class QaplaySettingsProvider
    {
        static readonly string[] RuleResults = { "", "passed", "failed", "flaky" };
        static readonly string[] RuleKinds = { "", "summary", "screenshot", "log", "data" };

        int m_NewResult, m_NewKind, m_NewDays = 365;
        bool m_NewForever;
        string m_NewTc = "", m_NewBuild = "", m_NewDevice = "";

        void DrawRetention()
        {
            Title(L("How long run reports are kept", "실행 리포트를 얼마나 둘지"));
            Hint(L("After every run, what is older than this is removed from the run reports folder. A run marked as kept (in the dashboard or with qaplay reports keep) is never removed.",
                "실행할 때마다 이 기한이 지난 것을 실행 리포트 폴더에서 지웁니다. 중요 표시를 한 실행(대시보드나 qaplay reports keep)은 지우지 않습니다."));
            Dictionary<string, object> retention = Part(m_Settings, "retention");
            Days(retention, "summary", L("Summary of a run", "실행의 요약"), 365);
            Days(retention, "passed", L("Evidence of passed rules (screens, logs)", "통과한 규칙의 증거(화면, 로그)"), 14);
            Days(retention, "failed", L("Evidence of failed or flaky rules", "실패하거나 불안정한 규칙의 증거"), 90);

            // ---- 조건을 붙인 기한: 맞는 것은 위의 기한과 이것 가운데 긴 쪽을 따른다 ----
            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField(L("Longer for some (the longest that matches wins)", "일부만 더 오래(맞는 것 가운데 가장 긴 기한)"), EditorStyles.miniBoldLabel);
            var rules = retention != null && retention.TryGetValue("rules", out object value) && value is List<object> list ? list : new List<object>();
            for (int n = 0; n < rules.Count; n++)
            {
                int at = n;
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(Describe(rules[n] as Dictionary<string, object>));
                    if (GUILayout.Button(L("Remove", "빼기"), GUILayout.Width(70)))
                        Change(json =>
                        {
                            var kept = new List<object>(rules);
                            kept.RemoveAt(at);
                            if (kept.Count == 0) Section(json, "retention").Remove("rules");
                            else Section(json, "retention")["rules"] = kept;
                        });
                }
            }
            if (rules.Count == 0) Hint(L("(none: the three above apply to everything)", "(없음: 위의 셋을 모두에 씁니다)"));

            using (new EditorGUILayout.HorizontalScope())
            {
                m_NewResult = EditorGUILayout.Popup(m_NewResult, new[] { L("any result", "모든 결과"), L("passed", "통과"), L("failed", "실패"), L("flaky", "불안정") }, GUILayout.Width(95));
                m_NewKind = EditorGUILayout.Popup(m_NewKind, new[] { L("anything", "모든 것"), L("summary", "요약"), L("screens", "화면"), L("logs", "로그"), L("numbers", "측정값") }, GUILayout.Width(85));
                m_NewTc = Labeled("TC", m_NewTc, 70);
                m_NewBuild = Labeled(L("build", "빌드"), m_NewBuild, 80);
                m_NewDevice = Labeled(L("device", "기기"), m_NewDevice, 80);
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                m_NewForever = EditorGUILayout.ToggleLeft(L("Forever", "영구"), m_NewForever, GUILayout.Width(60));
                using (new EditorGUI.DisabledScope(m_NewForever))
                    m_NewDays = Mathf.Max(0, EditorGUILayout.IntField(m_NewDays, GUILayout.Width(60)));
                GUILayout.Label(L("day(s)", "일"), GUILayout.Width(40));
                if (GUILayout.Button(L("Add", "더하기"), GUILayout.Width(80))) AddRule(rules);
                GUILayout.FlexibleSpace();
            }
            Hint(L("TC, build and device may use * for any text (TC-0*, 1.2.*). Leave them empty to match everything.",
                "TC, 빌드, 기기에는 *로 아무 글자를 적을 수 있습니다(TC-0*, 1.2.*). 비워 두면 모두에 맞습니다."));
        }

        static string Labeled(string label, string value, float width)
        {
            GUILayout.Label(label, GUILayout.ExpandWidth(false));
            return EditorGUILayout.TextField(value, GUILayout.Width(width));
        }

        // 날 수 하나. "영구"를 켜면 forever다
        void Days(Dictionary<string, object> retention, string key, string label, int fallback)
        {
            object value = retention != null && retention.TryGetValue(key, out object v) ? v : null;
            bool forever = "forever".Equals(value);
            int days = value is double number ? (int)number : fallback;
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PrefixLabel(label);
                bool foreverNow = EditorGUILayout.ToggleLeft(L("Forever", "영구"), forever, GUILayout.Width(60));
                int daysNow = days;
                using (new EditorGUI.DisabledScope(foreverNow))
                    daysNow = Mathf.Max(0, EditorGUILayout.DelayedIntField(days, GUILayout.Width(60)));
                GUILayout.Label(L("day(s)", "일"));
                if (foreverNow != forever || daysNow != days) Change(json => Section(json, "retention")[key] = foreverNow ? "forever" : (object)daysNow);
            }
        }

        void AddRule(List<object> rules)
        {
            var when = new Dictionary<string, object>();
            if (m_NewResult > 0) when["result"] = RuleResults[m_NewResult];
            if (m_NewKind > 0) when["kind"] = RuleKinds[m_NewKind];
            if (m_NewTc.Trim().Length > 0) when["tc"] = m_NewTc.Trim();
            if (m_NewBuild.Trim().Length > 0) when["build"] = m_NewBuild.Trim();
            if (m_NewDevice.Trim().Length > 0) when["device"] = m_NewDevice.Trim();
            var rule = new Dictionary<string, object> { ["when"] = when, ["days"] = m_NewForever ? "forever" : (object)m_NewDays };
            Change(json => Section(json, "retention")["rules"] = new List<object>(rules) { rule });
            GUI.FocusControl(null);
        }

        static string Describe(Dictionary<string, object> rule)
        {
            if (rule == null) return "?";
            var parts = new List<string>();
            if (rule.TryGetValue("when", out object value) && value is Dictionary<string, object> when)
                foreach (KeyValuePair<string, object> condition in when) parts.Add(condition.Key + " = " + condition.Value);
            object days = rule.TryGetValue("days", out object d) ? d : "?";
            return (parts.Count == 0 ? L("everything", "모든 것") : string.Join(", ", parts)) + "   →   "
                   + ("forever".Equals(days) ? L("forever", "영구") : Convert.ToString(days, System.Globalization.CultureInfo.InvariantCulture) + L(" day(s)", "일"));
        }
    }
}
