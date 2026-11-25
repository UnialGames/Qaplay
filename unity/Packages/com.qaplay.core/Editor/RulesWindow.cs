using System;
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
    /// 모두 Play 중인 게임 화면을 대상으로 한다.
    /// </summary>
    sealed class RulesWindow : EditorWindow
    {
        string m_Path = "";
        string m_Report = "";
        MessageType m_ReportType = MessageType.None;
        Vector2 m_Scroll;

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
            if (change == PlayModeStateChange.ExitingPlayMode) UguiRecorder.Stop();
            Repaint();
        }

        void OnGUI()
        {
            bool playing = Application.isPlaying;
            if (!playing)
                EditorGUILayout.HelpBox("Enter Play mode to record, check or run rules.", MessageType.Info);

            GUILayout.Label("Record", EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(!playing))
            {
                if (GUILayout.Button(UguiRecorder.IsRecording ? "Stop recording" : "Start recording"))
                {
                    if (UguiRecorder.IsRecording) UguiRecorder.Stop();
                    else UguiRecorder.Start();
                }
            }
            m_Scroll = EditorGUILayout.BeginScrollView(m_Scroll, GUILayout.MinHeight(120));
            EditorGUILayout.SelectableLabel(UguiRecorder.ToYaml(), EditorStyles.textArea, GUILayout.ExpandHeight(true));
            EditorGUILayout.EndScrollView();
            using (new EditorGUI.DisabledScope(UguiRecorder.Lines.Count == 0))
            {
                if (GUILayout.Button("Save recording as..."))
                {
                    string path = EditorUtility.SaveFilePanel("Save rule file", Application.dataPath, "recorded", "yaml");
                    if (path.Length > 0)
                    {
                        File.WriteAllText(path, UguiRecorder.ToYaml());
                        AssetDatabase.Refresh();
                        m_Path = path;
                        Report("saved " + path, MessageType.Info);
                    }
                }
            }

            EditorGUILayout.Space();
            GUILayout.Label("Rule file", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                m_Path = EditorGUILayout.TextField(m_Path);
                if (GUILayout.Button("...", GUILayout.Width(30)))
                {
                    string path = EditorUtility.OpenFilePanel("Open rule file", Application.dataPath, "yaml");
                    if (path.Length > 0) m_Path = path;
                }
            }
            using (new EditorGUI.DisabledScope(!playing || m_Path.Length == 0))
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Check")) Start(Check);
                if (GUILayout.Button("Run")) Start(Run);
            }
            if (m_Report.Length > 0) EditorGUILayout.HelpBox(m_Report, m_ReportType);
        }

        // 파일을 읽어 넘긴다. 읽지 못하거나 문법이 틀리면 그 이유를 보여 준다
        void Start(Action<Rule> action)
        {
            Rule rule;
            try
            {
                rule = RuleReader.Read(File.ReadAllText(m_Path));
            }
            catch (Exception e) when (e is IOException || e is FormatException || e is UnauthorizedAccessException)
            {
                Report(e.Message, MessageType.Error);
                return;
            }
            action(rule);
        }

        void Check(Rule rule)
        {
            Report("checking...", MessageType.None);
            AgentHost.Run(RuleChecker.Check(rule, AgentSession.Local().Call, (List<RuleIssue> issues) =>
                Report(issues.Count == 0 ? "check: no issues on the current screen" : "check: " + issues.Count + " issue(s)\n" + string.Join("\n", issues),
                    issues.Count == 0 ? MessageType.Info : MessageType.Warning)));
        }

        void Run(Rule rule)
        {
            Report("running...", MessageType.None);
            AgentHost.Run(RuleRunner.Run(rule, AgentSession.Local().Call, (RuleResult result) =>
                Report("run: " + result, result.passed ? MessageType.Info : MessageType.Error)));
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
