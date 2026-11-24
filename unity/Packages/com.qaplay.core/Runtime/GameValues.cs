using System;
using System.Collections.Generic;

namespace Qaplay
{
    /// <summary>
    /// get 명령으로 읽을 수 있는 게임 값(재화 등). 게임 코드가 키와 읽는 방법을 등록한다.
    /// <code>GameValues.Register("gold", () => player.gold);</code>
    /// </summary>
    public static class GameValues
    {
        static readonly Dictionary<string, Func<object>> s_Getters = new Dictionary<string, Func<object>>();

        /// <summary>같은 키를 다시 등록하면 새 것으로 바뀐다(씬을 다시 불러올 때 등).</summary>
        public static void Register(string key, Func<object> getter)
        {
            if (string.IsNullOrEmpty(key)) throw new ArgumentException("key is empty", nameof(key));
            s_Getters[key] = getter ?? throw new ArgumentNullException(nameof(getter));
        }

        public static void Unregister(string key) => s_Getters.Remove(key);

        /// <summary>
        /// 값을 읽는다. keys가 null이면 등록된 값을 모두 돌려준다.
        /// 등록되지 않은 키가 하나라도 있으면 아무것도 읽지 않고 unknown_key. 예외의 keys에 없는 키가 담긴다.
        /// </summary>
        public static Dictionary<string, object> Get(IEnumerable<string> keys = null)
        {
            // 읽는 도중 등록이 바뀌어도 되도록 복사본을 돈다
            var wanted = new List<string>(keys ?? s_Getters.Keys);
            List<string> missing = wanted.FindAll(key => key == null || !s_Getters.ContainsKey(key));
            if (missing.Count > 0)
                throw new QaplayException("unknown_key", "not registered: " + string.Join(", ", missing),
                    keys: missing.ToArray());

            var values = new Dictionary<string, object>();
            foreach (string key in wanted)
            {
                object value = s_Getters[key]();
                // 프로토콜로 보낼 수 있는 값만 받는다: 숫자, 문자열, 불리언, null
                if (!(value == null || value is string || value is bool
                      || value is int || value is long || value is float || value is double))
                    throw new QaplayException("internal",
                        "value of '" + key + "' must be a number, string, bool or null, but was " + value.GetType().Name);
                values[key] = value;
            }
            return values;
        }
    }
}
