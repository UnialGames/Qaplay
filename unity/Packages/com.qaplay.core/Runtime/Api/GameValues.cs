using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Qaplay.Core")]
[assembly: InternalsVisibleTo("Qaplay.UGUI")]

namespace Qaplay
{
    /// <summary>
    /// get 명령으로 읽을 수 있는 게임 값(재화 등). 게임 코드가 키와 읽는 방법을 등록한다.
    /// <code>GameValues.Register("gold", () => player.gold);</code>
    /// 이 어셈블리(Qaplay.Api)는 게임 코드가 참조하므로 출시 빌드에도 들어간다. 그래서 등록 호출은
    /// QA 빌드(QAPLAY 심벌)와 에디터에서만 컴파일되고, 출시 빌드에서는 호출 자체가 사라진다.
    /// 값을 읽는 쪽(GameValueReader)은 QA 전용 어셈블리에 있다.
    /// </summary>
    public static class GameValues
    {
        internal static readonly Dictionary<string, Func<object>> Getters = new Dictionary<string, Func<object>>();

        /// <summary>같은 키를 다시 등록하면 새 것으로 바뀐다(씬을 다시 불러올 때 등).</summary>
        [Conditional("QAPLAY"), Conditional("UNITY_EDITOR")]
        public static void Register(string key, Func<object> getter)
        {
            if (string.IsNullOrEmpty(key)) throw new ArgumentException("key is empty", nameof(key));
            Getters[key] = getter ?? throw new ArgumentNullException(nameof(getter));
        }

        [Conditional("QAPLAY"), Conditional("UNITY_EDITOR")]
        public static void Unregister(string key) => Getters.Remove(key);
    }
}
