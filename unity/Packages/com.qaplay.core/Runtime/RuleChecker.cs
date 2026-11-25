using System;
using System.Collections;
using System.Collections.Generic;

namespace Qaplay
{
    /// <summary>사전 검증에서 찾은 문제 하나. docs/rules.md 8절.</summary>
    public sealed class RuleIssue
    {
        /// <summary>몇 번째 명령인지(1부터).</summary>
        public int index;
        public RuleStep step;
        /// <summary>none: 걸리는 요소가 없음 / many: 하나여야 하는데 여럿 / unknown_key: 등록되지 않은 게임 값.</summary>
        public string kind;
        /// <summary>셀렉터에 걸린 요소 수. 범위(within)에서 막혔거나 게임 값 문제면 0.</summary>
        public int count;
        public string message;

        public override string ToString() =>
            "command " + index + " (line " + step.line + "): " + step.source + " -> " + kind + ": " + message;
    }

    /// <summary>
    /// 사전 검증기. 규칙을 실행하지 않고, 셀렉터를 지금 화면에 대어 걸리는 요소가 없거나 여럿인 명령을 찾는다.
    /// 아무것도 누르지 않는다. Unity에 기대지 않는 코드다. 러너도 같은 파일을 쓴다.
    /// </summary>
    // ponytail: 지금 화면 하나만 본다. 뒤 화면에서 나올 요소(팝업 안의 버튼 등)는 none으로 나온다.
    // 화면을 넘겨 가며 보려면 실행하면서 명령 직전에 검사하는 방식이 필요하다.
    public static class RuleChecker
    {
        /// <summary>돌려받은 코루틴을 끝까지 돌리면 done이 문제 목록과 함께 한 번 불린다. 문제가 없으면 빈 목록.</summary>
        public static IEnumerator Check(Rule rule, AgentCall call, Action<List<RuleIssue>> done)
        {
            var issues = new List<RuleIssue>();
            for (int n = 0; n < rule.steps.Count; n++)
            {
                RuleStep step = rule.steps[n];
                bool element = step.args.ContainsKey("selector");
                if (!element && step.cmd != "get") continue; // seed는 확인할 것이 없다

                // 요소는 find로 세어만 본다. 게임 값은 읽어도 바뀌는 것이 없으므로 get을 그대로 보낸다(값 비교는 하지 않는다)
                Dictionary<string, object> response = null;
                IEnumerator sending = element
                    ? call("find", new Dictionary<string, object> { ["selector"] = step.args["selector"] }, r => response = r)
                    : call(step.cmd, step.args, r => response = r);
                while (sending.MoveNext()) yield return sending.Current;

                var issue = new RuleIssue { index = n + 1, step = step };
                if (!true.Equals(response["ok"]))
                {
                    var error = (Dictionary<string, object>)response["error"];
                    string code = (string)error["code"];
                    issue.kind = code == "not_found" ? "none" : code == "ambiguous" ? "many" : code;
                    // find가 not_found나 ambiguous를 내는 것은 범위(within)가 하나로 정해지지 않을 때다
                    issue.message = (element ? "within: " : "") + error["message"];
                }
                else if (element)
                {
                    var result = (Dictionary<string, object>)response["result"];
                    issue.count = ((List<object>)result["matches"]).Count;
                    string resolves = (string)result["resolves"];
                    // 여럿이 문제인 것은 요소 하나가 필요한 tap뿐이다. 보임과 사라짐 확인은 여럿이어도 된다
                    if (resolves == "none" || (resolves == "many" && step.cmd == "tap"))
                    {
                        issue.kind = resolves;
                        issue.message = "selector matched " + issue.count + " elements";
                    }
                }
                if (issue.kind != null) issues.Add(issue);
            }
            done(issues);
        }
    }
}
