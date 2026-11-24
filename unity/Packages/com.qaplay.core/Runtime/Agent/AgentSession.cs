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
        static readonly string[] Commands = { "hello", "dump", "find", "tap", "wait", "get" };

        bool m_Hello;

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
                    result["matches"] = UiFinder.Find(UguiDumper.Dump(), Selector(args)).ConvertAll(m => m.ToData());
                    break;

                case "tap":
                {
                    Vector2 point = default;
                    IEnumerator find = UguiTap.WaitForPoint(Selector(args), Int(args, "timeoutMs") ?? 0, p => point = p);
                    while (find.MoveNext()) yield return find.Current;
                    IEnumerator tap = TouchInjector.Tap(point.x, point.y);
                    while (tap.MoveNext()) yield return tap.Current;
                    result["point"] = new Dictionary<string, object> { ["x"] = point.x, ["y"] = point.y };
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
