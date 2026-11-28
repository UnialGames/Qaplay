using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Qaplay.Runner
{
    /// <summary>
    /// 게임 밖(OS)에서만 알 수 있는 기기 지표를 읽는다: CPU 사용률, 프로세스 메모리(PSS), 발열 단계, 배터리.
    /// Android는 adb로, PC 빌드는 러너가 띄운 프로세스에서 읽는다. 에이전트의 명령이 아니다: 규칙의 device 요청을 러너가 가로챈다.
    /// </summary>
    sealed class DeviceMeter
    {
        /// <summary>한 번 읽은 값. 읽지 못한 것은 null.</summary>
        public struct Sample
        {
            /// <summary>프로세스가 지금까지 쓴 CPU 시간(초)과, 그것을 읽은 시각(초).</summary>
            public double? cpuSeconds, clock;
            public double? pss, batteryTemp;
            public int? thermal, battery;
        }

        const double MinCpuWindowSeconds = 1.0;

        readonly GameTarget m_Target;
        readonly string m_Package;
        readonly Stopwatch m_Clock = Stopwatch.StartNew();
        Sample m_Last;

        /// <param name="target">게임이 도는 곳. 어떤 지표를 어떻게 읽는지는 대상이 안다.</param>
        /// <param name="package">게임이 hello로 알려 준 앱의 이름. 대상이 앱의 이름을 모를 때 쓴다.</param>
        public DeviceMeter(GameTarget target, string package)
        {
            m_Target = target;
            m_Package = package;
        }

        /// <summary>RuleRunner에 넘기는 AgentCall의 device 몫. 응답 봉투의 모양은 에이전트의 것과 같다.</summary>
        public IEnumerator Call(Dictionary<string, object> args, Action<Dictionary<string, object>> onResponse)
        {
            bool start = args != null && args.TryGetValue("start", out object value) && true.Equals(value);
            onResponse(new Dictionary<string, object> { ["ok"] = true, ["result"] = Take(start) });
            yield break;
        }

        /// <summary>
        /// 지표를 읽는다. cpu는 앞의 호출부터 지금까지의 평균 사용률(%)이고 코어 하나를 다 쓰면 100이다.
        /// startOnly이면 CPU의 기준점만 잡고 나머지는 읽지 않는다(메모리 읽기는 느리다).
        /// </summary>
        public Dictionary<string, object> Take(bool startOnly)
        {
            Sample now = Read(startOnly);
            double? cpu = null;
            // 1초보다 짧은 구간은 재지 못한 것으로 둔다. OS가 CPU 시간을 10~16ms 단위로 세어서, 짧은 구간에서는 0%처럼 틀린 값이 나온다
            // (명령 하나짜리 규칙에서 0 %가 나와 예산을 그냥 통과했다). 프로세스가 바뀌어 CPU 시간이 줄어든 경우도 재지 못한 것이다
            if (now.cpuSeconds != null && m_Last.cpuSeconds != null && now.clock - m_Last.clock >= MinCpuWindowSeconds && now.cpuSeconds >= m_Last.cpuSeconds)
                cpu = Math.Round((now.cpuSeconds.Value - m_Last.cpuSeconds.Value) / (now.clock.Value - m_Last.clock.Value) * 100.0, 1);
            m_Last = now;
            return new Dictionary<string, object>
            {
                ["cpu"] = cpu, ["pss"] = now.pss, ["thermal"] = now.thermal, ["battery"] = now.battery, ["batteryTemp"] = now.batteryTemp,
            };
        }

        Sample Read(bool startOnly)
        {
            try
            {
                return m_Target.ReadOs(startOnly, m_Package, m_Clock.Elapsed.TotalSeconds);
            }
            catch (UsageException)
            {
                // 앱이 떠 있지 않거나 기기가 끊겼다: 읽지 못한 것으로 둔다
            }
            return default;
        }

        /// <summary>Android 셸이 돌려준 글에서 값을 읽는다. 없는 것은 null로 둔다.</summary>
        public static Sample Parse(string output)
        {
            var sample = new Sample();
            double? ticksPerSecond = Number(output, @"TCK\s+(\d+)");
            sample.clock = Number(output, @"UPTIME\s+([\d.]+)");
            // /proc/pid/stat: "pid (이름) 상태 ..." 이름에 공백과 괄호가 들 수 있어 마지막 ')' 뒤부터 센다. utime과 stime은 그 뒤 12, 13번째다
            Match stat = Regex.Match(output, @"STAT\s+\d+ \(.*\) (.+)");
            if (stat.Success && ticksPerSecond != null)
            {
                string[] fields = stat.Groups[1].Value.Split(' ');
                if (fields.Length > 12 && long.TryParse(fields[11], out long user) && long.TryParse(fields[12], out long system))
                    sample.cpuSeconds = (user + system) / ticksPerSecond.Value;
            }
            sample.thermal = (int?)Number(output, @"Thermal Status: (\d+)");
            sample.battery = (int?)Number(output, @"^\s*level: (\d+)");
            double? tenths = Number(output, @"^\s*temperature: (-?\d+)"); // 섭씨의 열 배로 적혀 있다
            if (tenths != null) sample.batteryTemp = tenths.Value / 10.0;
            double? pssKb = Number(output, @"TOTAL PSS:\s*(\d+)");
            if (pssKb != null) sample.pss = Math.Round(pssKb.Value / 1024.0, 1);
            return sample;
        }

        static double? Number(string text, string pattern)
        {
            Match match = Regex.Match(text, pattern, RegexOptions.Multiline);
            return match.Success ? double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : (double?)null;
        }

        /// <summary>읽은 지표를 한 줄로. 읽지 못한 것은 빼고, 하나도 없으면 그 까닭을 알린다.</summary>
        public static string Summary(Dictionary<string, object> device)
        {
            var parts = new List<string>();
            void Add(string key, string label, string unit)
            {
                if (device.TryGetValue(key, out object value) && value != null)
                    parts.Add(label + " " + Convert.ToString(value, CultureInfo.InvariantCulture) + unit);
            }
            Add("cpu", "cpu", " %");
            Add("pss", "pss", " MB");
            Add("thermal", "thermal", "");
            Add("battery", "battery", " %");
            Add("batteryTemp", "battery temperature", " C");
            return parts.Count > 0
                ? string.Join(", ", parts)
                : "not available (needs --device for an Android device, or --app for a PC build; cpu also needs a second or more to measure)";
        }
    }
}
