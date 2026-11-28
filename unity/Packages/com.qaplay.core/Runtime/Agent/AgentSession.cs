using System;
using System.Collections;
using System.Collections.Generic;
using Qaplay.Injection;
using Qaplay.UGUI;
using UnityEngine;

namespace Qaplay.Agent
{
    /// <summary>
    /// 연결 하나에 대응한다. 요청(JSON) 하나를 받아 명령을 실행하고 응답(JSON)을 돌려준다. protocol/README.md 2절, 6절.
    /// 요청은 한 번에 하나씩 넘긴다. 앞 요청의 Handle이 끝나기 전에 다음 요청을 넘기지 않는다.
    /// </summary>
    public sealed class AgentSession
    {
        public const int HeartbeatMs = 1000;
        /// <summary>tap의 count와 intervalMs 상한. protocol/README.md의 tap.</summary>
        public const int MaxTapCount = 100, MaxTapIntervalMs = 10000;
        static readonly string[] Commands =
        {
            "hello", "dump", "find", "tap", "tapTogether", "wait", "get", "memory", "frames", "layout", "screenshot", "ghosting", "mock", "seed",
            "report", "reports", "reportGet", "reportDelete",
        };

        bool m_Hello;
        long m_LastCallId;

        /// <summary>hello가 성공해 다른 명령을 받을 수 있는 상태인지.</summary>
        public bool HelloDone => m_Hello;

        /// <summary>프로세스 안에서 쓰는 세션. 같은 빌드끼리라 버전 협상(hello) 없이 바로 명령을 받는다.</summary>
        public static AgentSession Local() => new AgentSession { m_Hello = true };

        /// <summary>
        /// 프로세스 안에서 명령 하나를 보내고 응답 봉투를 돌려받는다. 소켓으로 올 때와 같은 길(Handle)을 거친다.
        /// RuleRunner에 넘기는 AgentCall이다.
        /// </summary>
        public IEnumerator Call(string cmd, Dictionary<string, object> args, Action<Dictionary<string, object>> onResponse)
        {
            var request = new Dictionary<string, object>
            {
                ["type"] = "req", ["id"] = ++m_LastCallId, ["cmd"] = cmd,
                ["args"] = args ?? new Dictionary<string, object>(),
            };
            return Handle(Json.Write(request), json => onResponse((Dictionary<string, object>)Json.Parse(json)));
        }

        /// <summary>
        /// 돌려받은 코루틴을 끝까지 돌리면 respond가 응답 JSON과 함께 한 번 불린다. tap과 wait는 여러 프레임이 걸린다.
        /// 응답할 수 없는 프레임(JSON이 아님, 요청이 아님, id를 읽을 수 없음)이면 바로 FormatException을 던진다.
        /// 이때 부르는 쪽은 연결을 끊는다.
        /// </summary>
        public IEnumerator Handle(string requestJson, Action<string> respond)
        {
            if (!(Json.Parse(requestJson) is Dictionary<string, object> request))
                throw new FormatException("frame is not a JSON object");
            if (!request.ContainsKey("type"))
                throw new FormatException("frame has no type");
            if (!(request.TryGetValue("id", out object id) && id is double number && number > 0 && number == Math.Floor(number)))
                throw new FormatException("frame has no usable id");
            return Process(request, (long)number, respond);
        }

        IEnumerator Process(Dictionary<string, object> request, long id, Action<string> respond)
        {
            var result = new Dictionary<string, object>();
            Dictionary<string, object> error = null;
            IEnumerator work = Run(request, result);
            while (true)
            {
                // 명령이 던진 오류를 응답으로 바꾼다. yield는 try 밖에 있어야 한다
                try
                {
                    if (!work.MoveNext()) break;
                }
                catch (QaplayException e)
                {
                    error = ErrorOf(e);
                    break;
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                    error = new Dictionary<string, object> { ["code"] = "internal", ["message"] = e.Message };
                    break;
                }
                yield return work.Current;
            }

            var response = new Dictionary<string, object> { ["type"] = "res", ["id"] = id, ["ok"] = error == null };
            if (error == null) response["result"] = result;
            else response["error"] = error;
            respond(Json.Write(response));
        }

        // 명령을 실행해 result를 채운다. 여러 프레임이 걸리는 명령은 안쪽 코루틴을 직접 한 단계씩 돌린다.
        // 안쪽 코루틴을 그대로 yield하면 Unity가 대신 돌려서, 거기서 난 오류를 Process가 잡지 못한다.
        IEnumerator Run(Dictionary<string, object> request, Dictionary<string, object> result)
        {
            if (!"req".Equals(request["type"]))
                throw Bad("type must be req");
            if (!(request.TryGetValue("cmd", out object cmdValue) && cmdValue is string cmd))
                throw Bad("cmd is missing");
            var args = new Dictionary<string, object>();
            if (request.TryGetValue("args", out object argsValue))
                args = argsValue as Dictionary<string, object> ?? throw Bad("args must be an object");

            if (cmd != "hello" && !m_Hello)
                throw new QaplayException("hello_required", "send hello first");

            switch (cmd)
            {
                case "hello":
                    Hello(args, result);
                    break;

                case "dump":
                    foreach (KeyValuePair<string, object> pair in UguiDumper.Dump().ToData())
                        result[pair.Key] = pair.Value;
                    break;

                case "find":
                {
                    List<UiMatch> matches = UiFinder.Find(UguiDumper.Dump(), Selector(args));
                    result["matches"] = matches.ConvertAll(m => m.ToData());
                    result["resolves"] = UiFinder.Resolves(matches);
                    break;
                }

                case "tap":
                {
                    int count = Int(args, "count") ?? 1;
                    int intervalMs = Int(args, "intervalMs") ?? 0;
                    if (count < 1 || count > MaxTapCount) throw Bad("count must be 1 to " + MaxTapCount);
                    if (intervalMs < 0 || intervalMs > MaxTapIntervalMs) throw Bad("intervalMs must be 0 to " + MaxTapIntervalMs);

                    Vector2[] points = null;
                    IEnumerator find = UguiTap.WaitForPoints(new[] { Selector(args) }, Int(args, "timeoutMs") ?? 0, p => points = p);
                    while (find.MoveNext()) yield return find.Current;
                    // 연타는 처음 정한 자리를 그대로 다시 누른다. 첫 클릭으로 버튼이 꺼지거나 가려져도 사람 손가락처럼 계속 누른다
                    IEnumerator tap = TouchInjector.Tap(points, count, intervalMs);
                    while (tap.MoveNext()) yield return tap.Current;
                    result["point"] = PointData(points[0]);
                    break;
                }

                case "tapTogether":
                {
                    if (!(args.TryGetValue("selectors", out object value) && value is List<object> list)
                        || list.Count < 2 || list.Count > TouchInjector.MaxPoints)
                        throw Bad("selectors must be an array of 2 to " + TouchInjector.MaxPoints + " selectors");
                    List<UiSelector> selectors = list.ConvertAll(
                        s => ToSelector(s as Dictionary<string, object> ?? throw Bad("selectors must be objects")));

                    Vector2[] points = null;
                    IEnumerator find = UguiTap.WaitForPoints(selectors, Int(args, "timeoutMs") ?? 0, p => points = p);
                    while (find.MoveNext()) yield return find.Current;
                    IEnumerator tap = TouchInjector.Tap(points, 1, 0);
                    while (tap.MoveNext()) yield return tap.Current;
                    result["points"] = Array.ConvertAll(points, PointData);
                    break;
                }

                case "wait":
                {
                    UiSelector selector = Selector(args);
                    string state = args.TryGetValue("state", out object s) ? s as string : null;
                    if (state == null) throw Bad("state is missing");
                    float start = Time.realtimeSinceStartup;
                    IEnumerator wait = UguiWait.Until(selector, state, Int(args, "timeoutMs") ?? throw Bad("timeoutMs is missing"));
                    while (wait.MoveNext()) yield return wait.Current;
                    result["elapsedMs"] = (int)((Time.realtimeSinceStartup - start) * 1000f);
                    break;
                }

                case "get":
                {
                    List<string> keys = null;
                    if (args.TryGetValue("keys", out object keysValue))
                    {
                        if (!(keysValue is List<object> list)) throw Bad("keys must be an array");
                        keys = list.ConvertAll(k => k as string ?? throw Bad("keys must be strings"));
                    }
                    result["values"] = GameValueReader.Get(keys);
                    break;
                }

                case "memory":
                {
                    IEnumerator read = MemoryReader.Read(result);
                    while (read.MoveNext()) yield return read.Current;
                    break;
                }

                case "frames":
                {
                    int hitchMs = Int(args, "hitchMs") ?? FrameMeter.DefaultHitchMs;
                    if (hitchMs < 1 || hitchMs > 10000) throw Bad("hitchMs must be 1 to 10000");
                    foreach (KeyValuePair<string, object> pair in FrameMeter.Take(hitchMs))
                        result[pair.Key] = pair.Value;
                    break;
                }

                case "layout":
                {
                    List<string> checks = null;
                    if (args.TryGetValue("checks", out object checksValue))
                    {
                        if (!(checksValue is List<object> list)) throw Bad("checks must be an array");
                        checks = list.ConvertAll(c => c is string check && Array.IndexOf(UguiLayout.Checks, check) >= 0
                            ? check
                            : throw Bad("checks can have: " + string.Join(", ", UguiLayout.Checks)));
                    }
                    // 한 프레임을 기다린다. 방금 생긴 요소도 한 번 그려진 뒤라야 무엇이 무엇 위에 있는지(그리는 순서)를 알 수 있다
                    yield return null;
                    result["issues"] = UguiLayout.Check(checks);
                    break;
                }

                case "ghosting":
                {
                    List<object> images = UguiGhosts.Take();
                    int frames = 0;
                    foreach (Dictionary<string, object> image in images) frames += (int)image["frames"];
                    result["images"] = images;
                    result["frames"] = frames;
                    break;
                }

                case "screenshot":
                {
                    byte[] png = null;
                    IEnumerator capture = BugReporter.CaptureScreen(shot => png = shot);
                    while (capture.MoveNext()) yield return capture.Current;
                    result["png"] = png == null ? null : Convert.ToBase64String(png);
                    result["screen"] = new Dictionary<string, object> { ["width"] = Screen.width, ["height"] = Screen.height };
                    break;
                }

                case "mock":
                {
                    Dictionary<string, object> set = null;
                    if (args.TryGetValue("set", out object setValue))
                        set = setValue as Dictionary<string, object> ?? throw Bad("set must be an object of mock name and scenario");
                    bool reset = args.TryGetValue("reset", out object resetValue) && (resetValue as bool? ?? throw Bad("reset must be true or false"));
                    result["mocks"] = MockControl.Apply(set, reset);
                    break;
                }

                case "seed":
                    // 도구가 직접 맞추는 것은 Unity 기본 랜덤뿐이다. System.Random이나 서버 난수는 프로젝트 몫이다
                    UnityEngine.Random.InitState(Int(args, "seed") ?? throw Bad("seed is missing"));
                    break;

                case "report":
                {
                    IEnumerator create = BugReporter.Create(Str(args, "title"), Str(args, "description"), id => result["id"] = id);
                    while (create.MoveNext()) yield return create.Current;
                    break;
                }

                case "reports":
                    result["reports"] = BugReporter.List();
                    break;

                case "reportGet":
                    foreach (KeyValuePair<string, object> pair in BugReporter.Get(Str(args, "id") ?? throw Bad("id is missing")))
                        result[pair.Key] = pair.Value;
                    break;

                case "reportDelete":
                    BugReporter.Delete(Str(args, "id") ?? throw Bad("id is missing"));
                    break;

                default:
                    throw new QaplayException("unknown_command", "unknown command: " + cmd);
            }
        }

        // ponytail: 토큰 검사(unauthorized)가 없다. 에이전트에 토큰 설정이 생기는 조각에서 추가한다.
        void Hello(Dictionary<string, object> args, Dictionary<string, object> result)
        {
            if (!(args.TryGetValue("protocol", out object p) && p is Dictionary<string, object> protocol))
                throw Bad("protocol is missing");
            int min = Int(protocol, "min") ?? throw Bad("protocol.min is missing");
            int max = Int(protocol, "max") ?? throw Bad("protocol.max is missing");
            // 에이전트가 지원하는 버전은 하나뿐이다
            if (min > QaplayInfo.ProtocolVersion || max < QaplayInfo.ProtocolVersion)
                throw new QaplayException("unsupported_protocol",
                    "agent supports protocol " + QaplayInfo.ProtocolVersion + " only");

            m_Hello = true;
            result["protocol"] = QaplayInfo.ProtocolVersion;
            result["agent"] = new Dictionary<string, object> { ["version"] = QaplayInfo.AgentVersion };
            result["app"] = new Dictionary<string, object>
                { ["id"] = Application.identifier, ["version"] = Application.version };
            result["unity"] = Application.unityVersion;
            result["platform"] = Application.platform.ToString();
            result["screen"] = new Dictionary<string, object> { ["width"] = Screen.width, ["height"] = Screen.height };
            result["ui"] = new[] { "ugui" };
            result["heartbeatMs"] = HeartbeatMs;
            result["commands"] = Commands;
        }

        static UiSelector Selector(Dictionary<string, object> args)
        {
            if (!(args.TryGetValue("selector", out object value) && value is Dictionary<string, object> map))
                throw Bad("selector is missing");
            return ToSelector(map);
        }

        static UiSelector ToSelector(Dictionary<string, object> map)
        {
            var selector = new UiSelector
            {
                id = Str(map, "id"),
                name = Str(map, "name"),
                type = Str(map, "type"),
                text = Str(map, "text"),
                index = Int(map, "index"),
            };
            if (map.TryGetValue("within", out object within))
                selector.within = ToSelector(within as Dictionary<string, object> ?? throw Bad("within must be an object"));
            return selector;
        }

        // 없으면 null, 있는데 종류가 다르면 bad_request
        static string Str(Dictionary<string, object> map, string key)
        {
            if (!map.TryGetValue(key, out object value)) return null;
            return value as string ?? throw Bad(key + " must be a string");
        }

        static int? Int(Dictionary<string, object> map, string key)
        {
            if (!map.TryGetValue(key, out object value)) return null;
            if (value is double d && d == Math.Floor(d) && Math.Abs(d) <= int.MaxValue) return (int)d;
            throw Bad(key + " must be an integer");
        }

        static QaplayException Bad(string message) => new QaplayException("bad_request", message);

        static object PointData(Vector2 point) => new Dictionary<string, object> { ["x"] = point.x, ["y"] = point.y };

        static Dictionary<string, object> ErrorOf(QaplayException e)
        {
            var data = new Dictionary<string, object>();
            if (e.reason != null) data["reason"] = e.reason;
            if (e.count != null) data["count"] = e.count.Value;
            if (e.keys != null) data["keys"] = e.keys;
            if (e.matches != null)
            {
                // 요소는 최대 10개까지만 싣는다 (명세 3절)
                var matches = new List<object>();
                for (int i = 0; i < e.matches.Count && i < 10; i++) matches.Add(e.matches[i].ToData());
                data["matches"] = matches;
            }
            if (e.code == "unsupported_protocol")
                data["min"] = data["max"] = QaplayInfo.ProtocolVersion;

            var error = new Dictionary<string, object> { ["code"] = e.code, ["message"] = e.Message };
            if (data.Count > 0) error["data"] = data;
            return error;
        }
    }
}
