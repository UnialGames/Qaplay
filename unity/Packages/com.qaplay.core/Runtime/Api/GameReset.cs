using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Qaplay
{
    /// <summary>
    /// 게임을 처음 상태로 되돌리는 방법. 규칙의 reset 명령이 부른다: 규칙은 한 게임에서 차례로 돌기 때문에,
    /// 앞 규칙이 바꿔 놓은 상태(재화, 열린 팝업 등)에 뒤 규칙이 기대지 않게 규칙 첫머리에서 되돌린다.
    /// 영역마다 이름을 붙여 여러 개를 등록할 수 있다.
    /// <code>GameReset.Register("shop", () => { gold = 1200; shopUi.Refresh(); });</code>
    /// GameValues와 같이 등록 호출은 QA 빌드(QAPLAY 심벌)와 에디터에서만 컴파일되고, 출시 빌드에서는 호출 자체가 사라진다.
    /// </summary>
    public static class GameReset
    {
        // 등록한 순서대로 부른다(앞 영역의 상태에 뒤 영역이 기댈 수 있다)
        internal static readonly List<KeyValuePair<string, Action>> Actions = new List<KeyValuePair<string, Action>>();

        /// <summary>같은 이름을 다시 등록하면 새 것으로 바뀐다(자리는 그대로).</summary>
        [Conditional("QAPLAY"), Conditional("UNITY_EDITOR")]
        public static void Register(string name, Action reset)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("name is empty", nameof(name));
            if (reset == null) throw new ArgumentNullException(nameof(reset));
            int at = Actions.FindIndex(pair => pair.Key == name);
            if (at >= 0) Actions[at] = new KeyValuePair<string, Action>(name, reset);
            else Actions.Add(new KeyValuePair<string, Action>(name, reset));
        }

        [Conditional("QAPLAY"), Conditional("UNITY_EDITOR")]
        public static void Unregister(string name) => Actions.RemoveAll(pair => pair.Key == name);
    }
}
