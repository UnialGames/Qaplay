using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Qaplay.Agent
{
    /// <summary>frames 명령. 프레임마다 걸린 시간을 담아 두었다가 평균, 느린 쪽 1%, 끊김 횟수를 낸다. protocol/README.md의 frames.</summary>
    public static class FrameMeter
    {
        public const int DefaultHitchMs = 50;
        // 60fps로 10분. 넘으면 오래된 프레임부터 덮어쓴다. 한 번 잡아 두고 다시 잡지 않는다(누수 검사의 잡음이 되지 않게)
        const int Capacity = 36000;

        static float[] s_Ms;
        static int s_Count;       // 재기 시작한 뒤의 프레임 수. Capacity를 넘을 수 있다
        static int s_LastFrame;   // 마지막으로 담은 프레임. 재는 코루틴이 살아 있는지 본다
        static double s_LastTime; // 그때의 실제 시각(초)

        /// <summary>
        /// 앞의 호출부터 지금까지의 프레임을 요약해 돌려주고 새로 재기 시작한다.
        /// 처음 부르면 그때부터 잰다: 아무도 부르지 않는 게임에서는 프레임마다 하는 일이 없다.
        /// </summary>
        public static Dictionary<string, object> Take(int hitchMs)
        {
            // 재는 코루틴은 숨은 게임오브젝트에서 돈다. Play를 껐다 켜면 사라지므로, 멈춰 있으면 다시 시작한다
            bool recording = s_Ms != null && Time.frameCount - s_LastFrame <= 1;
            if (!recording)
            {
                s_Ms = s_Ms ?? new float[Capacity];
                s_Count = 0;
                s_LastFrame = Time.frameCount;
                s_LastTime = Time.realtimeSinceStartupAsDouble;
                AgentHost.Run(Record());
            }

            int count = Math.Min(s_Count, Capacity);
            var result = new Dictionary<string, object>
            {
                ["count"] = count, ["seconds"] = 0.0, ["averageMs"] = null, ["p99Ms"] = null, ["maxMs"] = null,
                ["hitches"] = 0, ["hitchMs"] = hitchMs,
            };
            if (count > 0)
            {
                var sorted = new float[count];
                Array.Copy(s_Ms, sorted, count);
                Array.Sort(sorted);
                double sum = 0;
                int hitches = 0;
                foreach (float ms in sorted)
                {
                    sum += ms;
                    if (ms > hitchMs) hitches++;
                }
                result["seconds"] = Math.Round(sum / 1000.0, 2);
                result["averageMs"] = Math.Round(sum / count, 2);
                // 가장 가까운 순위 방식: 100개면 100번째(가장 느린 것), 200개면 198번째
                result["p99Ms"] = Math.Round(sorted[(int)Math.Ceiling(count * 0.99) - 1], 2);
                result["maxMs"] = Math.Round(sorted[count - 1], 2);
                result["hitches"] = hitches;
            }
            s_Count = 0;
            return result;
        }

        static IEnumerator Record()
        {
            while (true)
            {
                yield return null;
                // 앞 프레임의 같은 자리부터 지금까지 흐른 실제 시간을 시계로 직접 잰다.
                // Time.unscaledDeltaTime은 쓰지 않는다: 빌드에서는 화면 주사율에 맞춰 다듬어진 값이라 멈춤이 가려진다
                // (Windows 빌드에서 0.15초 멈춘 프레임이 Mono는 50ms, IL2CPP는 16.67ms로 나왔다)
                double now = Time.realtimeSinceStartupAsDouble;
                s_Ms[s_Count % Capacity] = (float)((now - s_LastTime) * 1000.0);
                s_LastTime = now;
                s_Count++;
                s_LastFrame = Time.frameCount;
            }
        }
    }
}
