using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;

namespace Qaplay
{
    /// <summary>
    /// 에이전트에 명령 하나를 보내고 응답 봉투(ok, result 또는 error)를 돌려받는 길.
    /// 에디터 안에서는 AgentSession.Call이고, 러너에서는 소켓이다.
    /// </summary>
    public delegate IEnumerator AgentCall(string cmd, Dictionary<string, object> args,
        Action<Dictionary<string, object>> onResponse);

    /// <summary>규칙 하나를 실행한 결과. docs/rules.md 5절.</summary>
    public sealed class RuleResult
    {
        public bool passed;
        /// <summary>실패한 명령이 몇 번째인지(1부터). 통과면 0.</summary>
        public int index;
        /// <summary>실패한 명령. 통과면 null.</summary>
        public RuleStep step;
        /// <summary>에이전트가 돌려준 오류 코드. assertValue의 값이 다르면 value_mismatch.</summary>
        public string code;
        public string message;
        /// <summary>leakCheck가 되풀이마다 잰 메모리(memory 명령의 결과 그대로, 바이트). leakCheck가 없으면 null.</summary>
        public List<Dictionary<string, object>> memory;
        /// <summary>leakCheck가 본 계층별 증가 추세(되풀이 한 번에 MB). 끝까지 되풀이하지 못했으면 null.</summary>
        public Dictionary<string, double> growth;
        /// <summary>마지막 assertFrameTime이 받은 프레임 시간 요약(frames 명령의 결과 그대로). 없으면 null.</summary>
        public Dictionary<string, object> frames;
        /// <summary>마지막 assertDevice가 받은 기기 지표(러너가 OS에서 읽은 값). 없으면 null.</summary>
        public Dictionary<string, object> device;

        public override string ToString() => passed
            ? "passed"
            : "failed at command " + index + " (line " + step.line + "): " + step.source + " -> " + code + ": " + message;
    }

    /// <summary>
    /// 읽어 둔 규칙을 위에서부터 차례로 실행한다. 명령 하나가 실패하면 거기서 멈춘다.
    /// Unity에 기대지 않는 코드다. 러너도 같은 파일을 쓴다.
    /// </summary>
    public static class RuleRunner
    {
        // 누수를 판정하는 계층. process는 기록만 한다: 가만히 있어도 OS가 수십 MB씩 올리고 내려서 추세를 가릴 수 없다
        // ponytail: 네이티브 플러그인만 새는 누수는 process에만 보이므로 잡지 못한다. 필요해지면 process에 따로 큰 허용치를 둔다
        static readonly string[] LeakLayers = { "managed", "native" };

        /// <summary>돌려받은 코루틴을 끝까지 돌리면 done이 결과와 함께 한 번 불린다.</summary>
        public static IEnumerator Run(Rule rule, AgentCall call, Action<RuleResult> done)
        {
            int leakAt = rule.steps.FindIndex(s => s.cmd == "leakCheck");
            RuleStep leak = leakAt < 0 ? null : rule.steps[leakAt];
            var result = new RuleResult { memory = leak == null ? null : new List<Dictionary<string, object>>() };

            // 프레임 시간이나 CPU 사용률처럼 구간을 재는 규칙이면, 규칙의 시작부터 재도록 지금 재기를 시작한다
            foreach (string meter in new[] { "frames", "device" })
            {
                int at = rule.steps.FindIndex(s => s.cmd == meter);
                if (at < 0 || result.code != null) continue;
                result.step = rule.steps[at];
                result.index = at + 1;
                // frames는 첫 요청이 시작이다. device는 시작만 하고 느린 지표는 읽지 않게 start를 붙인다
                var args = new Dictionary<string, object>();
                if (meter == "device") args["start"] = true;
                IEnumerator starting = Send(meter, args, call, result, _ => { });
                while (starting.MoveNext()) yield return starting.Current;
            }

            // leakCheck가 없으면 처음부터 끝까지 한 번(round 0). 있으면 그 위는 한 번, 그 아래는 repeat번 되풀이하며 메모리를 잰다
            for (int round = 0; round <= (leak?.repeat ?? 0) && result.code == null; round++)
            {
                int from = round == 0 ? 0 : leakAt + 1;
                int to = round == 0 && leak != null ? leakAt : rule.steps.Count;
                for (int n = from; n < to && result.code == null; n++)
                {
                    IEnumerator one = Execute(rule.steps[n], call, result);
                    while (one.MoveNext()) yield return one.Current;
                    if (result.code != null) result.index = n + 1;
                }
                if (round == 0) continue;

                if (result.code == null)
                {
                    result.step = leak;
                    result.index = leakAt + 1;
                    Dictionary<string, object> memory = null;
                    IEnumerator reading = Send("memory", new Dictionary<string, object>(), call, result, m => memory = m);
                    while (reading.MoveNext()) yield return reading.Current;
                    if (memory != null) result.memory.Add(memory);
                }
                if (result.code != null) result.message += " (repeat " + round + " of " + leak.repeat + ")";
            }

            if (result.code == null && leak != null)
            {
                result.growth = new Dictionary<string, double>();
                var leaks = new List<string>();
                foreach (string layer in LeakLayers)
                {
                    List<double> megabytes = result.memory.ConvertAll(m => Megabytes(m[layer]));
                    double growth = result.growth[layer] = GrowthPerRepeat(megabytes);
                    if (growth > leak.allow)
                        leaks.Add(layer + " grew " + Number(growth, "0.###") + " MB per repeat ("
                                  + Number(megabytes[0], "0.0") + " MB -> " + Number(megabytes[megabytes.Count - 1], "0.0") + " MB over "
                                  + leak.repeat + " repeats, allowed " + Number(leak.allow, "0.###") + " MB per repeat)");
                }
                if (leaks.Count > 0)
                {
                    result.code = "leak";
                    result.message = string.Join("; ", leaks);
                }
            }

            // Mock의 응답을 바꾼 규칙은 끝날 때 기본값으로 돌려놓는다. 다음 규칙이 앞 규칙이 고른 응답을 물려받으면 안 된다.
            // 이 요청의 성패는 규칙의 결과에 넣지 않는다
            if (rule.steps.Exists(s => s.cmd == "mock"))
            {
                IEnumerator resetting = Send("mock", new Dictionary<string, object> { ["reset"] = true }, call, new RuleResult(), _ => { });
                while (resetting.MoveNext()) yield return resetting.Current;
            }

            result.passed = result.code == null;
            if (result.passed)
            {
                result.step = null;
                result.index = 0;
            }
            done(result);
        }

        // 명령 하나를 보내고 판정한다. 실패하면 result의 step, code, message를 채운다
        static IEnumerator Execute(RuleStep step, AgentCall call, RuleResult result)
        {
            result.step = step;
            Dictionary<string, object> response = null;
            IEnumerator sending = Send(step.cmd, step.args, call, result, r => response = r);
            while (sending.MoveNext()) yield return sending.Current;
            if (response == null) yield break;

            if (step.expect != null)
            {
                result.message = Mismatch(step.expect, (Dictionary<string, object>)response["values"]);
                if (result.message != null) result.code = "value_mismatch";
            }
            else if (step.noIssues)
            {
                result.message = LayoutIssues((List<object>)response["issues"]);
                if (result.message != null) result.code = "layout";
            }
            else if (step.budget != null)
            {
                if (step.cmd == "frames") result.frames = response;
                if (step.cmd == "device") result.device = response;
                result.message = step.cmd == "frames" ? OverFrameBudget(step.budget, response)
                    : step.cmd == "device" ? OverDeviceBudget(step.budget, response)
                    : OverBudget(step.budget, response);
                if (result.message != null) result.code = "over_budget";
            }
        }

        const int MaxIssuesListed = 5;

        // 배치 문제를 한 줄로 모은다. 없으면 null. 많으면 앞의 몇 개만 적고 나머지는 수만 알린다
        static string LayoutIssues(List<object> issues)
        {
            if (issues.Count == 0) return null;
            var lines = new List<string>();
            for (int n = 0; n < issues.Count && n < MaxIssuesListed; n++) lines.Add(DescribeIssue((Dictionary<string, object>)issues[n]));
            if (issues.Count > MaxIssuesListed) lines.Add("and " + (issues.Count - MaxIssuesListed) + " more");
            return issues.Count + " issue(s): " + string.Join("; ", lines);
        }

        /// <summary>layout 명령이 돌려준 문제 하나를 사람이 읽는 한 줄로.</summary>
        public static string DescribeIssue(Dictionary<string, object> issue)
        {
            if ("overlap".Equals(issue["kind"]))
                return issue["path"] + " overlaps " + issue["otherPath"] + " ("
                       + Number(Convert.ToDouble(issue["share"], CultureInfo.InvariantCulture) * 100.0, "0") + "% of the smaller one)";
            var element = (Dictionary<string, object>)issue["element"];
            string what = issue["path"] + (element.TryGetValue("text", out object text) ? " " + Json.Write(text) : "");
            return "truncated".Equals(issue["reason"])
                ? "text is cut off in " + what
                : "text spills out of " + what;
        }

        // 기기 지표의 단위. 러너가 OS에서 읽어 주는 값이다
        static readonly Dictionary<string, string> DeviceUnits = new Dictionary<string, string>
        {
            ["cpu"] = " %", ["pss"] = " MB", ["thermal"] = "", ["batteryTemp"] = " C",
        };

        // 예산을 넘은 지표를 모은다. 읽을 수 없는 지표(null)에 예산을 적었으면 확인하지 못한 것이므로 실패로 알린다
        static string OverDeviceBudget(Dictionary<string, double> budget, Dictionary<string, object> device)
        {
            var over = new List<string>();
            foreach (KeyValuePair<string, double> pair in budget)
            {
                if (!device.TryGetValue(pair.Key, out object raw) || raw == null)
                {
                    over.Add(pair.Key + ": not available here");
                    continue;
                }
                double value = Convert.ToDouble(raw, CultureInfo.InvariantCulture);
                if (value > pair.Value)
                    over.Add(pair.Key + ": " + Number(value, "0.#") + DeviceUnits[pair.Key] + ", budget " + Number(pair.Value, "0.###") + DeviceUnits[pair.Key]);
            }
            return over.Count == 0 ? null : string.Join("; ", over);
        }

        // 예산을 넘은 지표를 모은다. 모두 예산 안이면 null. 잰 프레임이 없으면 확인하지 못한 것이므로 실패로 알린다
        static string OverFrameBudget(Dictionary<string, double> budget, Dictionary<string, object> frames)
        {
            if (Convert.ToInt32(frames["count"], CultureInfo.InvariantCulture) == 0) return "no frames were measured";
            var over = new List<string>();
            foreach (KeyValuePair<string, double> pair in budget)
            {
                bool hitches = pair.Key == "hitches";
                double value = Convert.ToDouble(frames[hitches ? "hitches" : pair.Key + "Ms"], CultureInfo.InvariantCulture);
                if (value <= pair.Value) continue;
                over.Add(hitches
                    ? "hitches: " + Number(value, "0") + " frame(s) over " + frames["hitchMs"] + " ms (longest " + Number(Convert.ToDouble(frames["maxMs"], CultureInfo.InvariantCulture), "0.0") + " ms), budget " + Number(pair.Value, "0")
                    : pair.Key + ": " + Number(value, "0.0") + " ms, budget " + Number(pair.Value, "0.###") + " ms");
            }
            return over.Count == 0 ? null : string.Join("; ", over);
        }

        // 요청 하나를 보낸다. 성공하면 결과 객체를 onResult로 넘기고, 실패 응답이면 result의 code와 message를 채운다
        static IEnumerator Send(string cmd, Dictionary<string, object> args, AgentCall call, RuleResult result,
            Action<Dictionary<string, object>> onResult)
        {
            Dictionary<string, object> response = null;
            IEnumerator sending = call(cmd, args, r => response = r);
            while (sending.MoveNext()) yield return sending.Current;

            if (true.Equals(response["ok"])) onResult((Dictionary<string, object>)response["result"]);
            else
            {
                var error = (Dictionary<string, object>)response["error"];
                result.code = (string)error["code"];
                result.message = (string)error["message"];
            }
        }

        // 기대값과 다른 키를 "키: expected 기대값, got 실제 값"으로 모은다. 모두 같으면 null.
        // 양쪽 모두 JSON에서 읽은 값(숫자는 double)이라 1100과 1100.0은 같다
        static string Mismatch(Dictionary<string, object> expect, Dictionary<string, object> values)
        {
            var diffs = new List<string>();
            foreach (KeyValuePair<string, object> pair in expect)
                if (!Equals(pair.Value, values[pair.Key]))
                    diffs.Add(pair.Key + ": expected " + Json.Write(pair.Value) + ", got " + Json.Write(values[pair.Key]));
            return diffs.Count == 0 ? null : string.Join("; ", diffs);
        }

        // 예산을 넘은 계층을 "계층: 쓴 양 MB, budget 예산 MB"로 모은다. 모두 예산 안이면 null.
        // 그 플랫폼에서 읽을 수 없는 계층(null)에 예산을 적었으면, 확인하지 못한 것이므로 실패로 알린다
        static string OverBudget(Dictionary<string, double> budget, Dictionary<string, object> memory)
        {
            var over = new List<string>();
            foreach (KeyValuePair<string, double> pair in budget)
            {
                if (!memory.TryGetValue(pair.Key, out object bytes) || bytes == null)
                {
                    over.Add(pair.Key + ": not available on this platform");
                    continue;
                }
                double megabytes = Megabytes(bytes);
                if (megabytes > pair.Value)
                    over.Add(pair.Key + ": " + Number(megabytes, "0.0") + " MB, budget " + Number(pair.Value, "0.###") + " MB");
            }
            return over.Count == 0 ? null : string.Join("; ", over);
        }

        /// <summary>
        /// 되풀이마다 잰 값의 증가 추세: 뒤쪽 절반에 그은 직선의 기울기(최소제곱), 되풀이 한 번에 늘어난 양이다.
        /// 앞쪽 절반은 버린다. 풀과 캐시가 차는 동안의 증가는 누수가 아니다.
        /// 처음과 끝만 빼는 것보다 한 번 튄 값에 덜 흔들린다.
        /// </summary>
        public static double GrowthPerRepeat(IList<double> samples)
        {
            int from = samples.Count / 2, count = samples.Count - from;
            if (count < 2) return 0;
            double meanX = (count - 1) / 2.0, meanY = 0;
            for (int i = 0; i < count; i++) meanY += samples[from + i] / count;
            double covariance = 0, variance = 0;
            for (int i = 0; i < count; i++)
            {
                covariance += (i - meanX) * (samples[from + i] - meanY);
                variance += (i - meanX) * (i - meanX);
            }
            return covariance / variance;
        }

        /// <summary>memory 명령이 돌려준 바이트 값을 MB로.</summary>
        public static double Megabytes(object bytes) => Convert.ToDouble(bytes, CultureInfo.InvariantCulture) / (1024.0 * 1024.0);

        static string Number(double value, string format) => value.ToString(format, CultureInfo.InvariantCulture);
    }
}
