using System.Collections.Generic;

namespace Qaplay
{
    /// <summary>get 명령. 게임 코드가 GameValues.Register로 등록한 값을 읽는다.</summary>
    public static class GameValueReader
    {
        /// <summary>
        /// 값을 읽는다. keys가 null이면 등록된 값을 모두 돌려준다.
        /// 등록되지 않은 키가 하나라도 있으면 아무것도 읽지 않고 unknown_key. 예외의 keys에 없는 키가 담긴다.
        /// </summary>
        public static Dictionary<string, object> Get(IEnumerable<string> keys = null)
        {
            // 읽는 도중 등록이 바뀌어도 되도록 복사본을 돈다
            var wanted = new List<string>(keys ?? GameValues.Getters.Keys);
            List<string> missing = wanted.FindAll(key => key == null || !GameValues.Getters.ContainsKey(key));
            if (missing.Count > 0)
                throw new QaplayException("unknown_key", "not registered: " + string.Join(", ", missing),
                    keys: missing.ToArray());

            var values = new Dictionary<string, object>();
            foreach (string key in wanted)
            {
                object value = GameValues.Getters[key]();
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

    /// <summary>게임이 등록한 되돌리기(GameReset)를 부른다. reset 명령이 쓴다.</summary>
    public static class GameResetRunner
    {
        /// <summary>
        /// names가 null이면 등록된 것을 모두, 아니면 그 이름들만 등록한 순서대로 부르고 부른 이름들을 돌려준다.
        /// 하나도 등록되지 않았으면 no_reset, 등록되지 않은 이름이 있으면 아무것도 부르지 않고 unknown_reset.
        /// </summary>
        public static List<string> Run(IList<string> names = null)
        {
            // 되돌리는 도중에 다시 등록해도(화면을 새로 만들며 등록하는 게임) 되도록 복사본을 돈다
            var all = new List<KeyValuePair<string, System.Action>>(GameReset.Actions);
            if (all.Count == 0)
                throw new QaplayException("no_reset", "the game registered no way to go back to its start (GameReset.Register in the game's code)");
            if (names != null)
            {
                List<string> missing = new List<string>(names).FindAll(name => !all.Exists(pair => pair.Key == name));
                if (missing.Count > 0)
                    throw new QaplayException("unknown_reset", "not registered: " + string.Join(", ", missing)
                                                               + " (registered: " + string.Join(", ", all.ConvertAll(pair => pair.Key)) + ")", keys: missing.ToArray());
            }
            var done = new List<string>();
            foreach (KeyValuePair<string, System.Action> pair in all)
            {
                if (names != null && !names.Contains(pair.Key)) continue;
                pair.Value();
                done.Add(pair.Key);
            }
            return done;
        }
    }
}
