// 이 파일의 API는 QA 빌드(QAPLAY 심벌)와 에디터에만 있다. Mock은 이 API를 읽어야 하므로,
// Mock 코드가 가드 없이 출시 빌드에 섞이면 컴파일이 되지 않는다. 출시 빌드에 Mock이 들어가는 것을 그렇게 막는다.
#if QAPLAY || UNITY_EDITOR
using System;
using System.Collections.Generic;

namespace Qaplay
{
    /// <summary>
    /// 테스트가 고르는 Mock의 응답 시나리오. 결제, 로그인, 광고처럼 OS나 스토어의 화면을 거쳐야 하는 SDK 호출은
    /// 게임 밖이라 도구가 누를 수 없다. 그 구간을 프로젝트의 Mock으로 바꾸고, 어떻게 응답할지를 테스트가 정한다.
    /// <code>
    /// Mocks.Register("store", "success", "fail", "cancel");   // 게임: Mock 지점과 응답의 종류를 알린다. 첫 번째가 기본값
    /// switch (Mocks.Scenario("store")) { ... }                // 프로젝트의 Mock: 지금 고른 응답대로 콜백을 부른다
    /// </code>
    /// 테스트는 규칙의 <c>mock: { store: cancel }</c>이나 Python의 <c>game.mock(store="cancel")</c>로 고른다.
    /// </summary>
    public static class Mocks
    {
        internal static readonly Dictionary<string, string[]> Scenarios = new Dictionary<string, string[]>();
        internal static readonly Dictionary<string, string> Current = new Dictionary<string, string>();

        /// <summary>Mock 지점을 등록한다. 첫 시나리오가 기본값이다. 같은 이름을 다시 등록하면 기본값으로 돌아간다.</summary>
        public static void Register(string name, params string[] scenarios)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("name is empty", nameof(name));
            if (scenarios == null || scenarios.Length == 0 || Array.IndexOf(scenarios, null) >= 0)
                throw new ArgumentException("at least one scenario is needed", nameof(scenarios));
            Scenarios[name] = (string[])scenarios.Clone();
            Current[name] = scenarios[0];
        }

        public static void Unregister(string name)
        {
            Scenarios.Remove(name);
            Current.Remove(name);
        }

        /// <summary>지금 고른 시나리오. 등록하지 않은 이름이면 InvalidOperationException.</summary>
        public static string Scenario(string name) =>
            Current.TryGetValue(name, out string scenario)
                ? scenario
                : throw new InvalidOperationException("mock '" + name + "' is not registered (Mocks.Register)");
    }
}
#endif
