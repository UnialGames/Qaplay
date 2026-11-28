using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Qaplay.Agent
{
    /// <summary>
    /// 테스터가 버그를 제보하는 방법: 단축키 하나와, 손가락 몇 개로 몇 초 누르기. 프로젝트 설정(qaplay.json)의 bugReport에서 고른다
    /// (Unity의 Project Settings > qaplay). 에디터에서는 그 파일을 바로 읽고, QA 빌드에는 빌드할 때 그 값이 들어간다(Resources의 qaplay-agent).
    /// </summary>
    public sealed class BugTrigger
    {
        /// <summary>QA 빌드에 들어가는 설정의 Resources 이름.</summary>
        public const string ResourceName = "qaplay-agent";

        /// <summary>제보하는 키. null이면 키로는 제보하지 않는다.</summary>
        public Key? key = Key.F12;
        /// <summary>함께 누르는 손가락 수. 0이면 손가락으로는 제보하지 않는다.</summary>
        public int fingers = 3;
        /// <summary>그만큼 누르고 있어야 하는 시간(초).</summary>
        public float seconds = 1f;

        static BugTrigger s_Current;

        /// <summary>지금 쓰는 방법. 처음 물을 때 설정을 읽는다(Play마다 다시). 테스트는 다른 것을 넣을 수 있다(null이면 다시 읽는다).</summary>
        public static BugTrigger Current
        {
            get => s_Current ??= Read();
            set => s_Current = value;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Forget() => s_Current = null;

        static BugTrigger Read()
        {
            string text = null;
#if UNITY_EDITOR
            string file = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "qaplay.json");
            if (File.Exists(file)) text = File.ReadAllText(file);
#else
            TextAsset asset = Resources.Load<TextAsset>(ResourceName);
            if (asset != null) text = asset.text;
#endif
            try
            {
                return Parse(text == null ? null : Json.Parse(text) as Dictionary<string, object>);
            }
            catch (FormatException e)
            {
                // 설정을 읽지 못해도 테스터는 제보할 수 있어야 한다: 기본 방법(F12, 세 손가락 1초)으로 둔다
                Debug.LogWarning("[qaplay] the bug report setting cannot be read, F12 and three fingers for 1 second are used: " + e.Message);
                return new BugTrigger();
            }
        }

        /// <summary>설정 전체(qaplay.json)에서 bugReport 부분을 읽는다. 없으면 기본값. 틀리면 FormatException.</summary>
        public static BugTrigger Parse(Dictionary<string, object> settings)
        {
            var trigger = new BugTrigger();
            if (settings == null || !settings.TryGetValue("bugReport", out object value)) return trigger;
            var part = value as Dictionary<string, object> ?? throw new FormatException("bugReport must be an object");
            if (part.TryGetValue("key", out object key))
            {
                if (!(key is string name)) throw new FormatException("bugReport.key must be the name of a key (F12) or none");
                if (name == "none" || name.Length == 0) trigger.key = null;
                else if (Enum.TryParse(name, true, out Key parsed) && parsed != Key.None) trigger.key = parsed;
                else throw new FormatException("bugReport.key: no such key: " + name);
            }
            if (part.TryGetValue("fingers", out object fingers))
            {
                if (!(fingers is double count) || count != Math.Floor(count) || count < 0 || count == 1 || count > 10)
                    throw new FormatException("bugReport.fingers must be 0 (off) or 2 to 10");
                trigger.fingers = (int)count;
            }
            if (part.TryGetValue("seconds", out object seconds))
            {
                if (!(seconds is double time) || time < 0.2 || time > 10) throw new FormatException("bugReport.seconds must be from 0.2 to 10");
                trigger.seconds = (float)time;
            }
            return trigger;
        }

        /// <summary>사람이 읽는 한 줄(제보했다는 안내 등에 쓴다).</summary>
        public string Describe()
        {
            var ways = new List<string>();
            if (key != null) ways.Add(key.ToString());
            if (fingers > 0) ways.Add(fingers + " fingers for " + seconds + "s");
            return ways.Count == 0 ? "off" : string.Join(" or ", ways);
        }
    }
}
