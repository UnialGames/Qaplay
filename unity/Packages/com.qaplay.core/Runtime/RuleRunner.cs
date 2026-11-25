using System;
using System.Collections;
using System.Collections.Generic;

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
        /// <summary>돌려받은 코루틴을 끝까지 돌리면 done이 결과와 함께 한 번 불린다.</summary>
        public static IEnumerator Run(Rule rule, AgentCall call, Action<RuleResult> done)
        {
            for (int n = 0; n < rule.steps.Count; n++)
            {
                RuleStep step = rule.steps[n];
                Dictionary<string, object> response = null;
                IEnumerator sending = call(step.cmd, step.args, r => response = r);
                while (sending.MoveNext()) yield return sending.Current;

                string code = null, message = null;
                if (!true.Equals(response["ok"]))
                {
                    var error = (Dictionary<string, object>)response["error"];
                    code = (string)error["code"];
                    message = (string)error["message"];
                }
                else if (step.expect != null)
                {
                    message = Mismatch(step.expect, (Dictionary<string, object>)((Dictionary<string, object>)response["result"])["values"]);
                    if (message != null) code = "value_mismatch";
                }

                if (code != null)
                {
                    done(new RuleResult { index = n + 1, step = step, code = code, message = message });
                    yield break;
                }
            }
            done(new RuleResult { passed = true });
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
    }
}
