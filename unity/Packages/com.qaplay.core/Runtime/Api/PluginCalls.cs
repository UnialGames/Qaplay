using System.Collections.Generic;
using System.Diagnostics;

namespace Qaplay
{
    /// <summary>
    /// SDK나 네이티브 플러그인 호출이 제대로 끝났는지 세어 둔다: 몇 번 불렀고, 콜백이 몇 번 왔고, 얼마나 걸렸는지.
    /// 프로젝트가 SDK를 부르는 자리에 두 줄을 넣는다.
    /// <code>
    /// PluginCalls.Started("store.buy");
    /// sdk.Buy(id, result => { PluginCalls.Finished("store.buy", result.ok); ... });
    /// </code>
    /// 센 값은 게임 값으로 읽힌다(get, 규칙의 assertValue): plugin.이름.started, .succeeded, .failed, .pending, .lastMs.
    /// pending은 아직 콜백이 오지 않은 호출 수다. 콜백이 두 번 오면 음수가 된다.
    /// GameValues.Register처럼 QA 빌드와 에디터에서만 컴파일되고, 출시 빌드에서는 호출 자체가 사라진다.
    /// </summary>
    // ponytail: 콜백이 다른 스레드에서 오는 SDK는 Finished를 메인 스레드에서 부르게 한다(여기에는 잠금이 없다).
    // 같은 이름의 호출이 겹치면 lastMs는 마지막으로 시작한 호출부터 잰 시간이다.
    public static class PluginCalls
    {
        sealed class Counts
        {
            public int started, succeeded, failed;
            public long startedAt;
            public double lastMs;
        }

        static readonly Dictionary<string, Counts> s_Calls = new Dictionary<string, Counts>();

        /// <summary>
        /// 이 이름의 호출을 0부터 센다. 부르기 전에도 값을 읽을 수 있게 된다("한 번도 부르지 않았다"를 확인할 때 필요하다).
        /// Started는 처음 보는 이름을 스스로 등록하므로, 그런 확인이 없으면 부르지 않아도 된다.
        /// </summary>
        [Conditional("QAPLAY"), Conditional("UNITY_EDITOR")]
        public static void Track(string name)
        {
            var counts = s_Calls[name] = new Counts();
            string key = "plugin." + name + ".";
            GameValues.Register(key + "started", () => counts.started);
            GameValues.Register(key + "succeeded", () => counts.succeeded);
            GameValues.Register(key + "failed", () => counts.failed);
            GameValues.Register(key + "pending", () => counts.started - counts.succeeded - counts.failed);
            GameValues.Register(key + "lastMs", () => counts.lastMs);
        }

        /// <summary>SDK를 부르기 직전에.</summary>
        [Conditional("QAPLAY"), Conditional("UNITY_EDITOR")]
        public static void Started(string name)
        {
            Counts counts = Of(name);
            counts.started++;
            counts.startedAt = Stopwatch.GetTimestamp();
        }

        /// <summary>SDK의 콜백이 왔을 때. 실패, 취소, 오류 응답은 succeeded에 false를 준다.</summary>
        [Conditional("QAPLAY"), Conditional("UNITY_EDITOR")]
        public static void Finished(string name, bool succeeded = true)
        {
            Counts counts = Of(name);
            if (succeeded) counts.succeeded++;
            else counts.failed++;
            counts.lastMs = (Stopwatch.GetTimestamp() - counts.startedAt) * 1000.0 / Stopwatch.Frequency;
        }

        static Counts Of(string name)
        {
            if (!s_Calls.ContainsKey(name)) Track(name);
            return s_Calls[name];
        }
    }
}
