using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Qaplay
{
    /// <summary>규칙 파일의 명령 하나. 에이전트에 보낼 요청(cmd, args)으로 이미 바뀌어 있다. docs/rules.md 4절.</summary>
    public sealed class RuleStep
    {
        /// <summary>파일의 몇 번째 줄인지(1부터).</summary>
        public int line;
        /// <summary>그 줄의 원문. 실패를 알릴 때 쓴다.</summary>
        public string source;
        /// <summary>프로토콜 명령 이름: tap, tapTogether, wait, get, memory, seed.</summary>
        public string cmd;
        public Dictionary<string, object> args;
        /// <summary>assertValue에서 키별 기대값. 다른 명령에서는 null.</summary>
        public Dictionary<string, object> expect;
        /// <summary>
        /// 예산을 확인하는 명령에서 지표별 상한. 다른 명령에서는 null. 무엇의 예산인지는 cmd로 가린다:
        /// memory는 계층(managed, native, process)별 MB, frames는 average와 p99(ms)와 hitches(횟수),
        /// device는 cpu(%), pss(MB), thermal(단계), batteryTemp(섭씨).
        /// </summary>
        public Dictionary<string, double> budget;
        /// <summary>assertLayout: 응답의 issues가 비어 있어야 통과다.</summary>
        public bool noIssues;
        /// <summary>leakCheck에서 아래 명령들을 되풀이할 횟수. 다른 명령에서는 0.</summary>
        public int repeat;
        /// <summary>leakCheck에서 되풀이 한 번에 늘어도 되는 메모리(MB).</summary>
        public double allow;
    }

    public sealed class Rule
    {
        public string tc;
        public string name;
        public readonly List<RuleStep> steps = new List<RuleStep>();
    }

    /// <summary>
    /// 규칙 파일(YAML)을 읽어 명령 목록으로 바꾼다. docs/rules.md.
    /// 읽는 YAML은 그 문서에 나온 모양뿐이다: 주석, ---, "키: 값", "- 명령: 인자", 한 줄짜리 { }와 [ ].
    /// Unity에 기대지 않는 코드다. 러너도 같은 파일을 쓴다.
    /// </summary>
    // ponytail: 여러 줄로 들여 쓴 중첩, 앵커 같은 나머지 YAML은 읽지 않고 오류로 알린다.
    // 그런 규칙 파일이 실제로 필요해지면 YamlDotNet으로 바꾼다.
    public static class RuleReader
    {
        public const int DefaultTimeoutMs = 10000;
        // 프로토콜의 상한과 같다(tap의 count와 intervalMs, tapTogether의 selectors)
        const int MaxRepeat = 100, MaxDelayMs = 10000, MaxTogether = 10;
        // 누수 검사: 뒤쪽 절반으로 추세를 보므로 너무 적게 되풀이하면 판정할 수 없다
        const int MinLeakRepeat = 10, MaxLeakRepeat = 1000;
        /// <summary>leakCheck에 allow를 적지 않았을 때, 되풀이 한 번에 늘어도 되는 메모리(MB).</summary>
        public const double DefaultLeakAllowMb = 0.05;
        // assertLayout이 고를 수 있는 검사(프로토콜 layout의 checks)
        static readonly string[] LayoutChecks = { "overflow" };
        const int MaxDepth = 16;
        const string OneLineHint = "nested blocks are not supported; write the arguments on one line inside { }";

        /// <summary>문법이 틀리면 한 줄도 실행하기 전에 FormatException. 메시지는 "rule line N: 이유"다.</summary>
        public static Rule Read(string yaml)
        {
            if (yaml == null) throw new FormatException("rule is null");
            string[] lines = yaml.TrimStart('﻿').Split('\n');

            int separator = -1;
            for (int n = 0; n < lines.Length; n++)
            {
                if (lines[n].TrimEnd() != "---") continue;
                if (separator >= 0) throw Bad(n, "only one '---' is allowed");
                separator = n;
            }

            var rule = new Rule();
            for (int n = 0; n < lines.Length; n++)
            {
                string raw = lines[n].TrimEnd(); // 줄 끝의 \r도 여기서 떨어진다
                string line = raw.TrimStart();
                if (n == separator || line.Length == 0 || line[0] == '#') continue;
                try
                {
                    if (n < separator) Header(rule, raw);
                    else rule.steps.Add(Step(raw, line, n + 1));
                }
                catch (FormatException e)
                {
                    throw Bad(n, e.Message);
                }
            }
            if (rule.steps.Count == 0) throw new FormatException("rule has no commands");

            // leakCheck는 요청이 아니라 경계다: 그 아래의 명령들이 되풀이할 묶음이다
            int leak = rule.steps.FindIndex(s => s.cmd == "leakCheck");
            if (leak >= 0)
            {
                int again = rule.steps.FindIndex(leak + 1, s => s.cmd == "leakCheck");
                if (again >= 0) throw Bad(rule.steps[again].line - 1, "only one leakCheck is allowed");
                if (leak == rule.steps.Count - 1) throw Bad(rule.steps[leak].line - 1, "leakCheck needs commands after it to repeat");
            }
            return rule;
        }

        static FormatException Bad(int index, string what) => new FormatException("rule line " + (index + 1) + ": " + what);

        static void Header(Rule rule, string raw)
        {
            if (raw[0] == '-') throw new FormatException("commands go after '---'");
            if (char.IsWhiteSpace(raw[0])) throw new FormatException(OneLineHint);
            int i = 0;
            string key = Key(raw, ref i);
            if (key != "tc" && key != "name") throw new FormatException("unknown header field '" + key + "'");
            string text = Text(Value(raw, ref i, 0, false), key);
            End(raw, i);
            if ((key == "tc" ? rule.tc : rule.name) != null) throw new FormatException("'" + key + "' is written twice");
            if (key == "tc") rule.tc = text;
            else rule.name = text;
        }

        static RuleStep Step(string raw, string line, int number)
        {
            if (!line.StartsWith("- ", StringComparison.Ordinal))
                throw new FormatException(char.IsWhiteSpace(raw[0])
                    ? OneLineHint
                    : "expected '- command: arguments' (header fields need '---' after them)");
            int i = 1;
            string command = Key(line, ref i);
            object arg = Value(line, ref i, 0, false);
            End(line, i);
            RuleStep step = Convert(command, arg);
            step.line = number;
            step.source = line;
            return step;
        }

        // ---- 규칙 명령 → 프로토콜 요청 ----

        static RuleStep Convert(string command, object arg)
        {
            var step = new RuleStep();
            switch (command)
            {
                case "tapOn":
                    step.cmd = "tap";
                    step.args = Target(arg, true);
                    break;
                case "tapTogether":
                    if (!(arg is List<object> targets) || targets.Count < 2 || targets.Count > MaxTogether)
                        throw new FormatException("tapTogether needs a list of 2 to " + MaxTogether + " elements: [ { name: A }, { name: B } ]");
                    step.cmd = "tapTogether";
                    step.args = new Dictionary<string, object>
                    {
                        // 글자 하나는 tapOn에서처럼 text 조건의 줄임이다
                        ["selectors"] = targets.ConvertAll(target => (object)(target is Dictionary<string, object> map
                            ? Selector(map)
                            : new Dictionary<string, object> { ["text"] = Text(target, "an element of tapTogether") })),
                        ["timeoutMs"] = DefaultTimeoutMs,
                    };
                    break;
                case "assertVisible":
                case "assertNotVisible":
                    step.cmd = "wait";
                    step.args = Target(arg, false);
                    step.args["state"] = command == "assertVisible" ? "visible" : "gone";
                    break;
                case "assertValue":
                    if (!(arg is Dictionary<string, object> expect) || expect.Count == 0)
                        throw new FormatException("assertValue needs { key: value }");
                    foreach (KeyValuePair<string, object> pair in expect)
                        if (pair.Value is Dictionary<string, object> || pair.Value is List<object>)
                            throw new FormatException("the value of '" + pair.Key + "' must be a number, text, true, false or null");
                    step.cmd = "get";
                    step.args = new Dictionary<string, object> { ["keys"] = new List<string>(expect.Keys) };
                    step.expect = expect;
                    break;
                case "assertMemory":
                    if (!(arg is Dictionary<string, object> budgets) || budgets.Count == 0)
                        throw new FormatException("assertMemory needs { managed | native | process: megabytes }");
                    step.cmd = "memory";
                    step.args = new Dictionary<string, object>();
                    step.budget = new Dictionary<string, double>();
                    foreach (KeyValuePair<string, object> pair in budgets)
                    {
                        if (pair.Key != "managed" && pair.Key != "native" && pair.Key != "process")
                            throw new FormatException("unknown memory layer '" + pair.Key + "'; use managed, native or process");
                        // 단위는 MB 하나다. 64MB처럼 단위를 붙여 쓰면 글자로 읽히므로 여기서 걸린다
                        if (!(pair.Value is double megabytes) || megabytes <= 0)
                            throw new FormatException("the budget of '" + pair.Key + "' must be a number of megabytes greater than 0, without a unit");
                        step.budget[pair.Key] = megabytes;
                    }
                    break;
                case "assertFrameTime":
                {
                    if (!(arg is Dictionary<string, object> frameBudgets))
                        throw new FormatException("assertFrameTime needs { average | p99: milliseconds, hitches: count }");
                    step.cmd = "frames";
                    step.args = new Dictionary<string, object>();
                    step.budget = new Dictionary<string, double>();
                    foreach (KeyValuePair<string, object> pair in frameBudgets)
                    {
                        switch (pair.Key)
                        {
                            case "average":
                            case "p99":
                                if (!(pair.Value is double ms) || ms <= 0)
                                    throw new FormatException("the budget of '" + pair.Key + "' must be a number of milliseconds greater than 0, without a unit");
                                step.budget[pair.Key] = ms;
                                break;
                            case "hitches":
                                step.budget["hitches"] = Integer(pair.Value, "hitches", 0);
                                break;
                            case "hitchOver":
                                step.args["hitchMs"] = Integer(pair.Value, "hitchOver", 1, 10000);
                                break;
                            default:
                                throw new FormatException("unknown frame time field '" + pair.Key + "'; use average, p99, hitches or hitchOver");
                        }
                    }
                    if (step.budget.Count == 0)
                        throw new FormatException("assertFrameTime needs { average | p99: milliseconds, hitches: count }");
                    break;
                }
                case "assertLayout":
                {
                    // 무엇을 검사할지: all(모두) 또는 검사 이름 하나
                    string check = arg as string;
                    if (check != "all" && Array.IndexOf(LayoutChecks, check) < 0)
                        throw new FormatException("assertLayout needs what to check: all, " + string.Join(", ", LayoutChecks));
                    step.cmd = "layout";
                    step.args = new Dictionary<string, object>();
                    if (check != "all") step.args["checks"] = new List<string> { check };
                    step.noIssues = true;
                    break;
                }
                case "mock":
                {
                    if (!(arg is Dictionary<string, object> scenarios) || scenarios.Count == 0)
                        throw new FormatException("mock needs { name: scenario }");
                    var set = new Dictionary<string, object>();
                    foreach (KeyValuePair<string, object> pair in scenarios)
                        set[pair.Key] = Text(pair.Value, "the scenario of '" + pair.Key + "'");
                    step.cmd = "mock";
                    step.args = new Dictionary<string, object> { ["set"] = set };
                    break;
                }
                case "assertDevice":
                {
                    if (!(arg is Dictionary<string, object> deviceBudgets) || deviceBudgets.Count == 0)
                        throw new FormatException("assertDevice needs { cpu: percent, pss: megabytes, thermal: level, batteryTemp: celsius }");
                    step.cmd = "device";
                    step.args = new Dictionary<string, object>();
                    step.budget = new Dictionary<string, double>();
                    foreach (KeyValuePair<string, object> pair in deviceBudgets)
                    {
                        switch (pair.Key)
                        {
                            case "cpu":
                            case "pss":
                            case "batteryTemp":
                                if (!(pair.Value is double number) || number <= 0)
                                    throw new FormatException("the budget of '" + pair.Key + "' must be a number greater than 0, without a unit");
                                step.budget[pair.Key] = number;
                                break;
                            case "thermal":
                                step.budget["thermal"] = Integer(pair.Value, "thermal", 0, 6);
                                break;
                            default:
                                throw new FormatException("unknown device field '" + pair.Key + "'; use cpu, pss, thermal or batteryTemp");
                        }
                    }
                    break;
                }
                case "leakCheck":
                {
                    // 횟수 하나, 또는 { repeat: 횟수, allow: 되풀이 한 번에 늘어도 되는 MB }
                    object repeat = arg;
                    step.allow = DefaultLeakAllowMb;
                    if (arg is Dictionary<string, object> options)
                    {
                        var rest = new Dictionary<string, object>(options);
                        if (!rest.Remove("repeat", out repeat)) throw new FormatException("leakCheck needs repeat: { repeat: 20 }");
                        if (rest.Remove("allow", out object allow))
                            step.allow = allow is double megabytes && megabytes >= 0
                                ? megabytes
                                : throw new FormatException("allow must be a number of megabytes, 0 or greater, without a unit");
                        foreach (string key in rest.Keys) throw new FormatException("unknown leakCheck field '" + key + "'");
                    }
                    step.cmd = "leakCheck";
                    step.args = new Dictionary<string, object>();
                    step.repeat = Integer(repeat, "the repeat count of leakCheck", MinLeakRepeat, MaxLeakRepeat);
                    break;
                }
                case "seed":
                    step.cmd = "seed";
                    step.args = new Dictionary<string, object> { ["seed"] = Integer(arg, "seed", int.MinValue) };
                    break;
                default:
                    throw new FormatException("unknown command '" + command + "'");
            }
            return step;
        }

        // 요소를 다루는 명령의 인자: 글자 하나(text 조건의 줄임) 또는 { 조건들, timeout }. tapOn은 repeat와 delay도 받는다
        static Dictionary<string, object> Target(object arg, bool tap)
        {
            if (arg is List<object>) throw new FormatException("a list [ ] is only for tapTogether");
            if (!(arg is Dictionary<string, object> map))
                return new Dictionary<string, object>
                {
                    ["selector"] = new Dictionary<string, object> { ["text"] = Text(arg, "the argument") },
                    ["timeoutMs"] = DefaultTimeoutMs,
                };

            var conditions = new Dictionary<string, object>(map);
            var args = new Dictionary<string, object> { ["selector"] = null, ["timeoutMs"] = DefaultTimeoutMs };
            if (conditions.Remove("timeout", out object timeout)) args["timeoutMs"] = Integer(timeout, "timeout", 0);
            if (tap && conditions.Remove("repeat", out object repeat))
                args["count"] = Integer(repeat, "repeat", 1, MaxRepeat);
            if (tap && conditions.Remove("delay", out object delay))
                args["intervalMs"] = Integer(delay, "delay", 0, MaxDelayMs);
            args["selector"] = Selector(conditions);
            return args;
        }

        static Dictionary<string, object> Selector(Dictionary<string, object> map)
        {
            var selector = new Dictionary<string, object>();
            foreach (KeyValuePair<string, object> pair in map)
            {
                switch (pair.Key)
                {
                    case "id":
                    case "name":
                    case "type":
                    case "text":
                        selector[pair.Key] = Text(pair.Value, pair.Key);
                        break;
                    case "index":
                        selector["index"] = Integer(pair.Value, "index", 0);
                        break;
                    case "within":
                        selector["within"] = Selector(pair.Value as Dictionary<string, object>
                            ?? throw new FormatException("within must be { conditions }"));
                        break;
                    default:
                        throw new FormatException("unknown selector field '" + pair.Key + "'");
                }
            }
            if (selector.Count == 0) throw new FormatException("selector has no conditions");
            return selector;
        }

        // 따옴표 없는 100, true, null은 YAML에서 글자가 아니다. 조용히 글자로 바꾸지 않고 따옴표를 쓰게 한다
        static string Text(object value, string what) =>
            value as string ?? throw new FormatException(what + " must be text; put quotes around values like 100 or true");

        static int Integer(object value, string what, int min, int max = int.MaxValue)
        {
            if (value is double d && d == Math.Floor(d) && d >= min && d <= max) return (int)d;
            throw new FormatException(what + " must be a whole number"
                + (max != int.MaxValue ? ", " + min + " to " + max : min == 0 ? ", 0 or greater" : ""));
        }

        // ---- 한 줄 안의 YAML ----

        // "키:"를 읽고 그 뒤로 옮긴다
        static string Key(string s, ref int i)
        {
            SkipSpace(s, ref i);
            string key;
            if (i < s.Length && (s[i] == '"' || s[i] == '\''))
            {
                key = Quoted(s, ref i);
                SkipSpace(s, ref i);
                if (i >= s.Length || s[i] != ':') throw new FormatException("expected ':' after '" + key + "'");
            }
            else
            {
                int colon = s.IndexOf(':', i);
                key = colon < 0 ? "" : s.Substring(i, colon - i).Trim();
                if (key.Length == 0 || key.IndexOfAny(" \t{}[],#\"'".ToCharArray()) >= 0)
                    throw new FormatException("expected 'key: value' at '" + s.Substring(i).Trim() + "'");
                i = colon;
            }
            i++; // :
            if (i < s.Length && s[i] != ' ' && s[i] != '\t')
                throw new FormatException("put a space after '" + key + ":'");
            return key;
        }

        static object Value(string s, ref int i, int depth, bool flow)
        {
            SkipSpace(s, ref i);
            if (i < s.Length && s[i] == '{') return Map(s, ref i, depth);
            if (i < s.Length && s[i] == '[') return Sequence(s, ref i, depth);
            if (i < s.Length && (s[i] == '"' || s[i] == '\'')) return Quoted(s, ref i);

            // 따옴표 없는 값. 주석(공백 뒤의 #)이나, { }와 [ ] 안이면 , } ] 앞에서 끝난다
            int start = i;
            while (i < s.Length && !(flow && (s[i] == ',' || s[i] == '}' || s[i] == ']'))
                                && !(s[i] == '#' && char.IsWhiteSpace(s[i - 1]))) i++;
            string text = s.Substring(start, i - start).Trim();
            if (text.Length == 0) throw new FormatException("a value is missing (" + OneLineHint + ")");
            if ("[]{}&*!|>%@`".IndexOf(text[0]) >= 0 || (flow && text.IndexOfAny(new[] { '{', '[' }) >= 0))
                throw new FormatException("unsupported YAML at '" + text + "'; put quotes around it if it is text");
            if (text.Contains(": ") || text.EndsWith(":", StringComparison.Ordinal))
                throw new FormatException("put quotes around text that contains ':' ('" + text + "')");

            if (text == "true") return true;
            if (text == "false") return false;
            if (text == "null" || text == "~") return null;
            bool numeric = false;
            foreach (char c in text)
            {
                if ("+-.eE0123456789".IndexOf(c) < 0) { numeric = false; break; }
                if (c >= '0' && c <= '9') numeric = true;
            }
            if (numeric && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)
                        && !double.IsInfinity(number))
                return number;
            return text;
        }

        // { 키: 값, ... }. 같은 줄에서 닫혀야 한다
        static Dictionary<string, object> Map(string s, ref int i, int depth)
        {
            if (depth > MaxDepth) throw new FormatException("nested too deeply");
            var map = new Dictionary<string, object>();
            i++; // {
            while (true)
            {
                SkipSpace(s, ref i);
                if (i >= s.Length) throw new FormatException("'{' must be closed with '}' on the same line");
                if (s[i] == '}') { i++; return map; }
                string key = Key(s, ref i);
                if (map.ContainsKey(key)) throw new FormatException("'" + key + "' is written twice");
                map[key] = Value(s, ref i, depth + 1, true);
                SkipSpace(s, ref i);
                if (i < s.Length && s[i] == ',') i++;
                else if (i < s.Length && s[i] != '}')
                    throw new FormatException("expected ',' or '}' at '" + s.Substring(i) + "'");
            }
        }

        // [ 값, ... ]. 같은 줄에서 닫혀야 한다
        static List<object> Sequence(string s, ref int i, int depth)
        {
            if (depth > MaxDepth) throw new FormatException("nested too deeply");
            var list = new List<object>();
            i++; // [
            while (true)
            {
                SkipSpace(s, ref i);
                if (i >= s.Length) throw new FormatException("'[' must be closed with ']' on the same line");
                if (s[i] == ']') { i++; return list; }
                list.Add(Value(s, ref i, depth + 1, true));
                SkipSpace(s, ref i);
                if (i < s.Length && s[i] == ',') i++;
                else if (i < s.Length && s[i] != ']')
                    throw new FormatException("expected ',' or ']' at '" + s.Substring(i) + "'");
            }
        }

        static string Quoted(string s, ref int i)
        {
            int start = i;
            if (s[i++] == '"')
            {
                while (i < s.Length && s[i] != '"') i += s[i] == '\\' ? 2 : 1;
                if (i >= s.Length) throw new FormatException("text is missing its closing \"");
                i++;
                // 큰따옴표 안의 이스케이프는 JSON과 같은 것만 받는다
                return (string)Json.Parse(s.Substring(start, i - start));
            }

            // 작은따옴표: '' 가 작은따옴표 한 글자이고 다른 이스케이프는 없다
            var sb = new StringBuilder();
            while (true)
            {
                if (i >= s.Length) throw new FormatException("text is missing its closing '");
                char c = s[i++];
                if (c != '\'') sb.Append(c);
                else if (i < s.Length && s[i] == '\'') { sb.Append('\''); i++; }
                else return sb.ToString();
            }
        }

        // 값 뒤에는 공백과 주석만 올 수 있다
        static void End(string s, int i)
        {
            SkipSpace(s, ref i);
            if (i < s.Length && s[i] != '#')
                throw new FormatException("unexpected text after the value: '" + s.Substring(i) + "'");
        }

        static void SkipSpace(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t')) i++;
        }
    }
}
