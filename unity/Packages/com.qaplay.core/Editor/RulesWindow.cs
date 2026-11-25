using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Qaplay.Agent;
using Qaplay.UGUI;
using UnityEditor;
using UnityEngine;

namespace Qaplay.Editor
{
    /// <summary>
    /// 규칙 파일을 손으로 다루는 창: 조작을 기록해 저장하고, 규칙 파일을 사전 검증하고 실행한다.
    /// 모두 Play 중인 게임 화면을 대상으로 한다. 게임이 멈춰 있으면 버튼이 게임을 먼저 시작한다.
    /// </summary>
    sealed class RulesWindow : EditorWindow
    {
        // 게임을 시작(또는 다시 시작)한 뒤에 이어서 할 일. Play로 들어가면 스크립트가 다시 읽혀 창의 값이 지워지므로
        // 에디터 세션에 적어 둔다: "record", "check", "run" 또는 빈 글자
        const string PendingKey = "qaplay.rules.pending";

        [SerializeField] string m_Path = "";
        string m_Report = "";
        MessageType m_ReportType = MessageType.None;
        Vector2 m_Scroll, m_Page;
        // 이 창에서 기록을 시작한 적이 있는지. 스크립트를 다시 읽으면(Play 시작 등) 기록이 지워지므로 이 값도 함께 지운다.
        // 에디터는 스크립트를 다시 읽을 때 private 값도 되살리므로, 되살리지 말라고 표시해 둔다
        [NonSerialized] bool m_Recorded;

        static string Pending
        {
            get => SessionState.GetString(PendingKey, "");
            set => SessionState.SetString(PendingKey, value);
        }

        [MenuItem("qaplay/Rules")]
        static void Open() => GetWindow<RulesWindow>("qaplay Rules");

        void OnEnable()
        {
            UguiRecorder.Changed += Repaint;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        void OnDisable()
        {
            UguiRecorder.Changed -= Repaint;
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
        }

        void OnPlayModeChanged(PlayModeStateChange change)
        {
            switch (change)
            {
                case PlayModeStateChange.ExitingPlayMode:
                    if (UguiRecorder.IsRecording && Pending.Length == 0)
                        Report("Recording stopped because the game stopped. Use \"Save recording to a file...\" to keep it.", MessageType.None);
                    UguiRecorder.Stop();
                    break;
                case PlayModeStateChange.EnteredEditMode:
                    // "Run from start": 게임을 끈 다음 다시 켠다
                    if (Pending.Length > 0) EditorApplication.update += EnterPlayWhenReady;
                    break;
                case PlayModeStateChange.EnteredPlayMode:
                    string pending = Pending;
                    Pending = "";
                    if (pending.Length > 0) Do(pending, true);
                    break;
            }
            Repaint();
        }

        // 게임을 끈 직후에는 에디터가 아직 정리 중이라 바로 켜라는 요청이 먹히지 않는다. 준비될 때까지 기다렸다가 켠다
        static void EnterPlayWhenReady()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            EditorApplication.update -= EnterPlayWhenReady;
            EditorApplication.EnterPlaymode();
        }

        // ---- 화면 ----

        void OnGUI()
        {
            bool playing = Application.isPlaying;
            bool changing = EditorApplication.isPlayingOrWillChangePlaymode != playing;
            m_Page = EditorGUILayout.BeginScrollView(m_Page);

            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                GUILayout.Label(playing ? "Game is running" : "Game is stopped", EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();
                using (new EditorGUI.DisabledScope(changing))
                {
                    if (GUILayout.Button(playing ? "Stop game" : "Start game", GUILayout.Width(100)))
                    {
                        if (playing) EditorApplication.ExitPlaymode();
                        else EditorApplication.EnterPlaymode();
                    }
                }
            }
            if (!playing) Hint("You can press the buttons below while the game is stopped. They start the game first when needed.");

            EditorGUILayout.Space();
            GUILayout.Label("1. Record your taps", EditorStyles.boldLabel);
            Hint("Press \"Start recording\", then click buttons in the Game view. Each element you click is added below " +
                 "as one line. When you are done, press \"Stop recording\" and save the lines to a file.");
            using (new EditorGUI.DisabledScope(changing))
            {
                if (GUILayout.Button(UguiRecorder.IsRecording ? "Stop recording" : "Start recording", GUILayout.Height(26)))
                    ToggleRecording();
            }

            // 이 창에서 시작한 기록만 보여 준다. 기록기는 테스트 같은 다른 곳에서도 쓰이므로,
            // 거기서 남은 줄을 이 창의 기록처럼 보여 주거나 저장하게 두지 않는다
            bool hasLines = m_Recorded && UguiRecorder.Lines.Count > 0;
            string shown = hasLines
                ? UguiRecorder.ToYaml()
                : UguiRecorder.IsRecording && m_Recorded
                    ? "Recording. Click buttons in the Game view."
                    : "Nothing recorded yet.";
            m_Scroll = EditorGUILayout.BeginScrollView(m_Scroll, GUILayout.MinHeight(110));
            EditorGUILayout.SelectableLabel(shown, EditorStyles.textArea, GUILayout.ExpandHeight(true));
            EditorGUILayout.EndScrollView();
            if (hasLines && UguiRecorder.Lines.Exists(line => line.StartsWith("#", StringComparison.Ordinal)))
                Hint("Lines starting with # are clicks that were not recorded: nothing at that spot takes a click. " +
                     "They are ignored when the rule runs.");
            using (new EditorGUI.DisabledScope(!hasLines))
            {
                if (GUILayout.Button("Save recording to a file..."))
                {
                    string path = EditorUtility.SaveFilePanel("Save rule file", Application.dataPath, "recorded", "yaml");
                    if (path.Length > 0) SaveRecording(path);
                }
            }

            EditorGUILayout.Space();
            GUILayout.Label("2. Check and run a rule file", EditorStyles.boldLabel);
            Hint("Choose a rule file (.yaml). Saving a recording in step 1 fills this in for you.");
            using (new EditorGUILayout.HorizontalScope())
            {
                m_Path = EditorGUILayout.TextField(m_Path);
                if (GUILayout.Button("Browse...", GUILayout.Width(80)))
                {
                    string path = EditorUtility.OpenFilePanel("Open rule file", Application.dataPath, "yaml");
                    if (path.Length > 0) m_Path = path;
                }
            }
            using (new EditorGUI.DisabledScope(changing || m_Path.Length == 0))
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Check", GUILayout.Height(26))) Request("check");
                if (GUILayout.Button("Run", GUILayout.Height(26))) Request("run");
                if (GUILayout.Button("Run from start", GUILayout.Height(26))) RunFromStart();
            }
            Hint("Check: taps nothing. Only looks whether the elements in the rule are on the current screen.\n" +
                 "Run: taps and checks on the current screen, then shows the result.\n" +
                 "Run from start: restarts the game, then runs. A recorded rule passes only from the screen where the recording started.");

            if (m_Report.Length > 0)
            {
                EditorGUILayout.Space();
                EditorGUILayout.HelpBox(m_Report, m_ReportType);
            }
            EditorGUILayout.EndScrollView();
        }

        static void Hint(string text) => EditorGUILayout.LabelField(text, EditorStyles.wordWrappedMiniLabel);

        // ---- 버튼이 하는 일 ----

        void ToggleRecording()
        {
            if (UguiRecorder.IsRecording)
            {
                UguiRecorder.Stop();
                Report("Recording stopped. Use \"Save recording to a file...\" to keep it.", MessageType.None);
            }
            else Request("record");
        }

        // 게임이 멈춰 있으면 먼저 게임을 시작하고, 시작된 뒤에 이어서 한다
        void Request(string action)
        {
            if (Application.isPlaying)
            {
                Do(action, false);
                return;
            }
            Pending = action;
            Report("Starting the game...", MessageType.None);
            EditorApplication.EnterPlaymode();
        }

        // 게임을 처음 상태로 되돌린 뒤 실행한다
        void RunFromStart()
        {
            if (!Application.isPlaying)
            {
                Request("run");
                return;
            }
            Pending = "run";
            Report("Restarting the game...", MessageType.None);
            EditorApplication.ExitPlaymode();
        }

        // justStarted: 게임이 방금 시작됐다. 첫 화면이 만들어질 때까지 몇 프레임 기다린 뒤에 한다
        void Do(string action, bool justStarted)
        {
            switch (action)
            {
                case "record":
                    UguiRecorder.Start();
                    m_Recorded = true;
                    Report("Recording. Click buttons in the Game view.", MessageType.None);
                    break;
                case "check":
                    ReadThen(rule => Check(rule, justStarted));
                    break;
                case "run":
                    ReadThen(rule => Run(rule, justStarted));
                    break;
            }
        }

        void SaveRecording(string path)
        {
            File.WriteAllText(path, UguiRecorder.ToYaml());
            AssetDatabase.Refresh();
            m_Path = path;
            Report("Saved: " + path + "\nYou can check and run it in step 2 below.", MessageType.Info);
        }

        // 파일을 읽어 넘긴다. 읽지 못하거나 문법이 틀리면 그 이유를 보여 준다
        void ReadThen(Action<Rule> action)
        {
            Rule rule;
            try
            {
                rule = RuleReader.Read(File.ReadAllText(m_Path));
            }
            catch (Exception e) when (e is IOException || e is FormatException || e is UnauthorizedAccessException)
            {
                Report("Could not read the rule file: " + e.Message, MessageType.Error);
                return;
            }
            action(rule);
        }

        void Check(Rule rule, bool justStarted)
        {
            Report("Checking...", MessageType.None);
            Launch(justStarted, RuleChecker.Check(rule, AgentSession.Local().Call, (List<RuleIssue> issues) =>
            {
                if (issues.Count == 0) Report("Check passed: every element in the rule is on the current screen.", MessageType.Info);
                else Report("Check found " + issues.Count + " issue(s) on the current screen\n" + string.Join("\n", issues), MessageType.Warning);
            }));
        }

        void Run(Rule rule, bool justStarted)
        {
            Report("Running...", MessageType.None);
            Launch(justStarted, RuleRunner.Run(rule, AgentSession.Local().Call, (RuleResult result) =>
            {
                if (result.passed) Report("Run passed: all " + rule.steps.Count + " command(s) succeeded.", MessageType.Info);
                else Report("Run failed: " + result, MessageType.Error);
            }));
        }

        static void Launch(bool justStarted, IEnumerator work) => AgentHost.Run(justStarted ? AfterFirstFrames(work) : work);

        static IEnumerator AfterFirstFrames(IEnumerator work)
        {
            for (int frame = 0; frame < 3; frame++) yield return null;
            while (work.MoveNext()) yield return work.Current;
        }

        void Report(string text, MessageType type)
        {
            m_Report = text;
            m_ReportType = type;
            if (type == MessageType.Error) Debug.LogError("[qaplay] " + text);
            else if (type != MessageType.None) Debug.Log("[qaplay] " + text);
            Repaint();
        }
    }
}
