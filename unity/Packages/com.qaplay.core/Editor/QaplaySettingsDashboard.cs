using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Sockets;
using UnityEditor;
using UnityEngine;
using static Qaplay.Editor.QaplayUi;

namespace Qaplay.Editor
{
    // 설정 화면의 "대시보드" 부분: 설정의 dashboard(포트, 화면의 언어, 추이에 쓰는 실행 수와 측정값)와, 대시보드를 띄워 브라우저로 여는 버튼.
    // 대시보드는 러너(qaplay dashboard)가 띄우는 이 PC의 작은 웹 서버다. 에디터를 닫으면 함께 멈춘다
    sealed partial class QaplaySettingsProvider
    {
        const string DashboardPid = "qaplay.dashboard.pid";

        // 추이로 그릴 수 있는 측정값(러너의 Dashboard.Metrics와 같은 이름)
        static readonly (string name, string english, string korean)[] Metrics =
        {
            ("frames.averageMs", "Frame time, average", "프레임 시간 평균"), ("frames.p99Ms", "Frame time, slowest 1%", "프레임 시간 상위 1%"),
            ("frames.hitches", "Hitches", "끊김 횟수"), ("growth.managed", "Managed memory growth per repeat", "반복당 관리 메모리 증가"),
            ("growth.native", "Native memory growth per repeat", "반복당 네이티브 메모리 증가"), ("device.cpu", "CPU", "CPU"),
            ("device.pss", "Memory (Android PSS)", "메모리(Android PSS)"), ("device.batteryTemp", "Battery temperature", "배터리 온도"),
        };

        string m_DashboardNote;

        [InitializeOnLoadMethod]
        static void StopDashboardOnQuit() => EditorApplication.quitting += () =>
        {
            Process dashboard = StartedDashboard();
            if (dashboard == null) return;
            try { dashboard.Kill(); }
            catch (InvalidOperationException) { }
        };

        // 이 에디터가 띄운 대시보드(스크립트를 다시 읽어도 기억한다). 없거나 끝났으면 null
        static Process StartedDashboard()
        {
            int pid = SessionState.GetInt(DashboardPid, 0);
            if (pid == 0) return null;
            try
            {
                Process process = Process.GetProcessById(pid);
                return process.HasExited || !process.ProcessName.StartsWith("qaplay", StringComparison.OrdinalIgnoreCase) ? null : process;
            }
            catch (Exception e) when (e is ArgumentException || e is InvalidOperationException)
            {
                return null;
            }
        }

        static bool Answers(int port)
        {
            try
            {
                using var client = new TcpClient();
                return client.ConnectAsync("127.0.0.1", port).Wait(300) && client.Connected;
            }
            catch (Exception e) when (e is SocketException || e is AggregateException)
            {
                return false;
            }
        }

        void OpenDashboard(int port)
        {
            m_DashboardNote = null;
            if (!Answers(port))
            {
                var start = new ProcessStartInfo(QaplayRunner.Find(), "dashboard") { WorkingDirectory = ProjectFolder, UseShellExecute = false, CreateNoWindow = true };
                try
                {
                    SessionState.SetInt(DashboardPid, Process.Start(start).Id);
                }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception || e is InvalidOperationException)
                {
                    m_DashboardNote = L("The dashboard could not be started: ", "대시보드를 띄우지 못했습니다: ") + e.Message;
                    return;
                }
                var waited = Stopwatch.StartNew();
                while (!Answers(port) && waited.ElapsedMilliseconds < 8000)
                {
                    if (StartedDashboard() == null)
                    {
                        m_DashboardNote = L("The dashboard stopped right away. Check the settings (button at the top): the port may be in use.",
                            "대시보드가 바로 멈췄습니다. 맨 위의 \"설정 확인\"을 눌러 보세요. 포트를 다른 프로그램이 쓰고 있을 수 있습니다.");
                        return;
                    }
                    System.Threading.Thread.Sleep(200);
                }
            }
            Application.OpenURL("http://localhost:" + port + "/");
        }

        void DrawDashboard()
        {
            Title(L("Dashboard", "대시보드"));
            Hint(L("A page in your browser with the kept runs: results over time, the failed screens, and how the measurements change. It runs on this PC only.",
                "보관된 실행을 브라우저로 봅니다: 결과의 흐름, 실패한 화면, 측정값의 추이. 이 PC에서만 열립니다."));
            Dictionary<string, object> dashboard = Part(m_Settings, "dashboard");
            int port = Number(dashboard, "port", 27280);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(L("Open the dashboard", "대시보드 열기"), GUILayout.Height(26))) OpenDashboard(port);
                Process started = StartedDashboard();
                using (new EditorGUI.DisabledScope(started == null))
                    if (GUILayout.Button(L("Stop it", "멈추기"), GUILayout.Height(26), GUILayout.Width(90)))
                    {
                        try { started?.Kill(); }
                        catch (InvalidOperationException) { }
                        SessionState.EraseInt(DashboardPid);
                    }
            }
            if (m_DashboardNote != null) EditorGUILayout.HelpBox(m_DashboardNote, MessageType.Warning);

            string[] languages = { "auto", "en", "ko" };
            string language = Text(dashboard, "lang");
            int index = Mathf.Max(0, Array.IndexOf(languages, language.Length == 0 ? "auto" : language));
            int picked = EditorGUILayout.Popup(L("Language of the page", "화면의 언어"), index, new[] { L("Same as the browser", "브라우저와 같게"), "English", "한국어" });
            if (picked != index) Change(json => Section(json, "dashboard")["lang"] = languages[picked]);

            int runs = Number(dashboard, "trendRuns", 50);
            int runsNow = Mathf.Max(1, EditorGUILayout.DelayedIntField(new GUIContent(L("Runs in the trends", "추이에 쓰는 최근 실행 수")), runs));
            if (runsNow != runs) Change(json => Section(json, "dashboard")["trendRuns"] = runsNow);

            int portNow = Mathf.Clamp(EditorGUILayout.DelayedIntField(new GUIContent(L("Port", "포트"), L("Change it only when another program uses 27280.", "다른 프로그램이 27280을 쓸 때만 바꿉니다.")), port), 1, 65535);
            if (portNow != port) Change(json => Section(json, "dashboard")["port"] = portNow);

            // 추이로 그리는 측정값. 적지 않았으면 전부다
            var chosen = new List<string>();
            if (dashboard != null && dashboard.TryGetValue("metrics", out object value) && value is List<object> list)
                foreach (object name in list) chosen.Add(name as string);
            else foreach ((string name, _, _) in Metrics) chosen.Add(name);
            EditorGUILayout.LabelField(L("Measurements drawn in the trends", "추이로 그리는 측정값"), EditorStyles.miniBoldLabel);
            foreach ((string name, string english, string korean) in Metrics)
            {
                bool on = chosen.Contains(name);
                bool now = EditorGUILayout.ToggleLeft(L(english, korean) + "   (" + name + ")", on);
                if (now == on) continue;
                var next = new List<object>();
                foreach ((string other, _, _) in Metrics)
                    if (other == name ? now : chosen.Contains(other)) next.Add(other);
                Change(json => Section(json, "dashboard")["metrics"] = next);
            }
        }
    }
}
