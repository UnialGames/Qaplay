using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;

namespace Qaplay.Runner
{
    /// <summary>프레임 하나는 길이(4바이트, 빅 엔디언) + UTF-8 JSON이다. protocol/README.md 1절.</summary>
    static class Frames
    {
        public const int MaxBytes = 16 * 1024 * 1024;

        public static void Write(Stream stream, string json)
        {
            byte[] body = Encoding.UTF8.GetBytes(json);
            var frame = new byte[4 + body.Length];
            frame[0] = (byte)(body.Length >> 24);
            frame[1] = (byte)(body.Length >> 16);
            frame[2] = (byte)(body.Length >> 8);
            frame[3] = (byte)body.Length;
            Buffer.BlockCopy(body, 0, frame, 4, body.Length);
            stream.Write(frame, 0, frame.Length);
        }

        public static string Read(Stream stream)
        {
            var header = new byte[4];
            stream.ReadExactly(header, 0, 4);
            long length = ((long)header[0] << 24) | ((long)header[1] << 16) | ((long)header[2] << 8) | header[3];
            if (length > MaxBytes) throw new IOException("frame is larger than 16 MiB");
            var body = new byte[length];
            stream.ReadExactly(body, 0, (int)length);
            return Encoding.UTF8.GetString(body);
        }
    }

    /// <summary>
    /// 게임을 잃었다. Kind는 crash(연결이 끊김: 앱이 죽었다) 또는 freeze(연결은 살아 있는데 화면이 멈췄거나 응답이 없다).
    /// 이 연결은 더 쓸 수 없다.
    /// </summary>
    sealed class AgentLostException : IOException
    {
        public readonly string Kind;

        public AgentLostException(string kind, string message) : base(message)
        {
            Kind = kind;
        }
    }

    /// <summary>에이전트와의 연결 하나. 접속하면 바로 hello를 보낸다.</summary>
    sealed class AgentClient : IDisposable
    {
        // 명령의 제한 시간(timeoutMs)이 지나고도 이만큼 더 응답이 없으면 응답하지 않는 것으로 본다
        const int ResponseMarginMs = 10000;

        readonly TcpClient m_Tcp;
        readonly NetworkStream m_Stream;
        readonly int m_FreezeMs;
        long m_LastId;
        long m_Frame = -1;
        long m_FrameChangedAt;

        /// <summary>hello의 결과: 앱, 플랫폼, 화면 크기 등.</summary>
        public readonly Dictionary<string, object> Info;

        /// <summary>게임에서 난 C# 예외(메시지와 스택). 받은 순서대로 쌓인다. 읽은 쪽이 비운다.</summary>
        public readonly List<string> Exceptions = new List<string>();

        /// <param name="freezeMs">프레임 번호가 이만큼 그대로면 프리징으로 본다.</param>
        public AgentClient(string host, int port, int freezeMs)
        {
            m_FreezeMs = freezeMs;
            m_Tcp = new TcpClient { NoDelay = true };
            m_Tcp.Connect(host, port);
            m_Stream = m_Tcp.GetStream();
            m_Stream.ReadTimeout = ResponseMarginMs; // 도착하기 시작한 프레임의 나머지를 기다리는 한도

            Dictionary<string, object> response = Request("hello", new Dictionary<string, object>
            {
                ["protocol"] = new Dictionary<string, object> { ["min"] = 1, ["max"] = 1 },
                ["client"] = new Dictionary<string, object> { ["name"] = "qaplay-runner", ["version"] = "0.1.0" },
            });
            if (!true.Equals(response["ok"]))
            {
                var error = (Dictionary<string, object>)response["error"];
                throw new IOException("hello failed: " + error["code"] + ": " + error["message"]);
            }
            Info = (Dictionary<string, object>)response["result"];
        }

        /// <summary>
        /// 요청 하나를 보내고 그 응답을 기다린다. 기다리는 동안 오는 이벤트로 게임의 상태를 본다:
        /// 예외는 Exceptions에 쌓고, 생존 신호의 프레임 번호가 멈추면 프리징, 연결이 끊기면 크래시로 AgentLostException.
        /// </summary>
        public Dictionary<string, object> Request(string cmd, Dictionary<string, object> args)
        {
            long id = ++m_LastId;
            int waitMs = ResponseMarginMs;
            if (args != null && args.TryGetValue("timeoutMs", out object t)) waitMs += Convert.ToInt32(t);
            try
            {
                Frames.Write(m_Stream, Json.Write(new Dictionary<string, object>
                {
                    ["type"] = "req", ["id"] = id, ["cmd"] = cmd, ["args"] = args ?? new Dictionary<string, object>(),
                }));

                long deadline = Environment.TickCount64 + waitMs;
                m_FrameChangedAt = Environment.TickCount64; // 요청 사이에는 읽지 않았으므로 지금부터 잰다
                while (true)
                {
                    long now = Environment.TickCount64;
                    long untilFreeze = m_FrameChangedAt + m_FreezeMs - now;
                    if (untilFreeze <= 0)
                        throw new AgentLostException("freeze", "the game stopped at frame " + m_Frame + " for " + m_FreezeMs + " ms (during " + cmd + ")");
                    if (deadline - now <= 0)
                        throw new AgentLostException("freeze", "no response to " + cmd + " within " + waitMs + " ms");

                    // 프레임이 도착할 때까지만 기한을 걸고 기다린다. 읽는 도중에 끊으면 다음 프레임의 경계를 잃는다
                    long waitUs = Math.Min(untilFreeze, deadline - now) * 1000;
                    if (!m_Tcp.Client.Poll((int)Math.Min(waitUs, int.MaxValue), SelectMode.SelectRead)) continue;

                    var message = (Dictionary<string, object>)Json.Parse(Frames.Read(m_Stream));
                    if ("res".Equals(message["type"]))
                    {
                        if (Convert.ToInt64(message["id"]) == id) return message;
                    }
                    else if ("evt".Equals(message["type"])) Event(message);
                }
            }
            catch (Exception e) when (!(e is AgentLostException) && (e is IOException || e is SocketException || e is ObjectDisposedException))
            {
                throw new AgentLostException("crash", "the connection to the game was lost (during " + cmd + "): " + e.Message);
            }
        }

        void Event(Dictionary<string, object> message)
        {
            var data = message.TryGetValue("data", out object d) ? d as Dictionary<string, object> : null;
            if (data == null) return;
            switch (message["name"] as string)
            {
                case "heartbeat":
                    long frame = Convert.ToInt64(data["frame"]);
                    if (frame != m_Frame)
                    {
                        m_Frame = frame;
                        m_FrameChangedAt = Environment.TickCount64;
                    }
                    break;
                case "exception":
                    Exceptions.Add((data["message"] + "\n" + data["stack"]).TrimEnd());
                    break;
                // 모르는 이벤트는 무시한다 (명세 2절)
            }
        }

        /// <summary>RuleRunner와 RuleChecker에 넘기는 AgentCall. 응답이 올 때까지 이 자리에서 기다린다.</summary>
        public IEnumerator Call(string cmd, Dictionary<string, object> args, Action<Dictionary<string, object>> onResponse)
        {
            onResponse(Request(cmd, args));
            yield break;
        }

        public void Dispose() => m_Tcp.Close();
    }
}
