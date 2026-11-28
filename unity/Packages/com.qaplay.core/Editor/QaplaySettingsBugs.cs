using System;
using System.Collections.Generic;
using Qaplay.Agent;
using UnityEditor;
using UnityEngine;
using static Qaplay.Editor.QaplayUi;

namespace Qaplay.Editor
{
    // 설정 화면의 "테스터가 버그를 제보하는 방법" 부분: 설정의 bugReport. 게임 안의 에이전트가 읽는다(에디터의 Play는 바로, QA 빌드는 빌드할 때 넣는다)
    sealed partial class QaplaySettingsProvider
    {
        static readonly string[] ReportKeys = { "none", "F12", "F11", "F10", "F9", "F8", "Backquote", "Insert", "Pause", "ScrollLock" };
        static readonly int[] ReportFingers = { 0, 2, 3, 4, 5 };

        void DrawBugReport()
        {
            Title(L("How testers report a bug", "테스터가 버그를 제보하는 방법"));
            Hint(L("In a QA build (and in Play), a tester saves a bug report with a key or by holding fingers on the screen. The report keeps the screen, the last taps and the log; "
                   + "take them with qaplay bugs pull. Pick a way that the game itself does not use. A QA build gets this when it is built: build again after a change.",
                "QA 빌드(와 Play)에서 테스터가 키나 손가락으로 화면을 누르고 있으면 버그 리포트가 저장됩니다. 그 순간의 화면, 최근 조작, 로그가 들어가고 qaplay bugs pull로 가져옵니다. "
                + "게임이 쓰지 않는 방법을 고르세요. QA 빌드에는 빌드할 때 들어가므로 바꾼 뒤 다시 빌드합니다."));
            BugTrigger trigger;
            try
            {
                trigger = BugTrigger.Parse(m_Settings);
            }
            catch (FormatException e)
            {
                EditorGUILayout.HelpBox(e.Message, MessageType.Error);
                trigger = new BugTrigger();
            }

            string key = trigger.key == null ? "none" : trigger.key.ToString();
            var keys = new List<string>(ReportKeys);
            if (!keys.Contains(key)) keys.Add(key);
            int index = keys.IndexOf(key);
            string[] labels = keys.ConvertAll(name => name == "none" ? L("No key", "키로는 하지 않음") : name).ToArray();
            int picked = EditorGUILayout.Popup(L("Key", "키"), index, labels);
            if (picked != index) Change(json => Section(json, "bugReport")["key"] = keys[picked]);

            int fingers = Mathf.Max(0, Array.IndexOf(ReportFingers, trigger.fingers));
            int fingersNow = EditorGUILayout.Popup(L("Fingers held on the screen", "화면을 누르는 손가락"), fingers,
                Array.ConvertAll(ReportFingers, count => count == 0 ? L("Not by touch", "손가락으로는 하지 않음") : count + L(" fingers", "개")));
            if (fingersNow != fingers) Change(json => Section(json, "bugReport")["fingers"] = ReportFingers[fingersNow]);
            using (new EditorGUI.DisabledScope(trigger.fingers == 0))
            {
                float seconds = EditorGUILayout.DelayedFloatField(L("For how long (seconds)", "누르고 있는 시간(초)"), trigger.seconds);
                seconds = Mathf.Clamp(Mathf.Round(seconds * 10f) / 10f, 0.2f, 10f);
                if (Mathf.Abs(seconds - trigger.seconds) > 0.001f) Change(json => Section(json, "bugReport")["seconds"] = (double)seconds);
            }
            if (trigger.key == null && trigger.fingers == 0)
                EditorGUILayout.HelpBox(L("Testers cannot report bugs from the game: choose a key or fingers.", "테스터가 게임에서 제보할 수 없습니다: 키나 손가락을 고르세요."), MessageType.Warning);
        }
    }
}
