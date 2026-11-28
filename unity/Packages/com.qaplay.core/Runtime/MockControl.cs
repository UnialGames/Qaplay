using System.Collections.Generic;

namespace Qaplay
{
    /// <summary>mock 명령. 게임이 Mocks.Register로 등록한 Mock의 응답 시나리오를 보고, 고른다. protocol/README.md의 mock.</summary>
    public static class MockControl
    {
        /// <summary>
        /// reset이면 모두 기본값으로 돌린 뒤, set에 적힌 대로 고른다. 그리고 등록된 Mock 전부의 상태를 돌려준다.
        /// 등록되지 않은 이름이나 그 Mock에 없는 시나리오가 하나라도 있으면 아무것도 바꾸지 않고 unknown_mock.
        /// </summary>
        public static Dictionary<string, object> Apply(Dictionary<string, object> set, bool reset)
        {
            if (set != null)
                foreach (KeyValuePair<string, object> pair in set)
                {
                    if (!Mocks.Scenarios.TryGetValue(pair.Key, out string[] scenarios))
                        throw new QaplayException("unknown_mock",
                            "no mock named '" + pair.Key + "'; registered: " + Listed(Mocks.Scenarios.Keys), keys: new[] { pair.Key });
                    if (!(pair.Value is string scenario) || System.Array.IndexOf(scenarios, scenario) < 0)
                        throw new QaplayException("unknown_mock",
                            "mock '" + pair.Key + "' has no scenario " + Json.Write(pair.Value) + "; use " + Listed(scenarios), keys: new[] { pair.Key });
                }

            if (reset)
                foreach (KeyValuePair<string, string[]> pair in Mocks.Scenarios)
                    Mocks.Current[pair.Key] = pair.Value[0];
            if (set != null)
                foreach (KeyValuePair<string, object> pair in set)
                    Mocks.Current[pair.Key] = (string)pair.Value;

            var mocks = new Dictionary<string, object>();
            foreach (KeyValuePair<string, string[]> pair in Mocks.Scenarios)
                mocks[pair.Key] = new Dictionary<string, object> { ["scenario"] = Mocks.Current[pair.Key], ["scenarios"] = pair.Value };
            return mocks;
        }

        static string Listed(IEnumerable<string> names)
        {
            string joined = string.Join(", ", names);
            return joined.Length > 0 ? joined : "(none)";
        }
    }
}
