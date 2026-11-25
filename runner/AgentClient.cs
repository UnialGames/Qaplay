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

    /// <summary>에이전트와의 연결 하나. 접속하면 바로 hello를 보낸다.</summary>
    sealed class AgentClient : IDisposable
    {
        // 명령의 제한 시간(timeoutMs)이 지나고도 이만큼 더 응답이 없으면 연결이 죽은 것으로 본다
        const int ResponseMarginMs = 10000;

        readonly TcpClient m_Tcp;
        readonly NetworkStream m_Stream;
        long m_LastId;

        /// <summary>hello의 결과: 앱, 플랫폼, 화면 크기 등.</summary>
        public readonly Dictionary<string, object> Info;

        public AgentClient(string host, int port)
        {
            m_Tcp = new TcpClient { NoDelay = true };
            m_Tcp.Connect(host, port);
            m_Stream = m_Tcp.GetStream();

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

        /// <summary>요청 하나를 보내고 그 응답을 기다린다. 사이에 오는 이벤트는 건너뛴다. 연결에 문제가 생기면 IOException.</summary>
        public Dictionary<string, object> Request(string cmd, Dictionary<string, object> args)
        {
            long id = ++m_LastId;
            Frames.Write(m_Stream, Json.Write(new Dictionary<string, object>
            {
                ["type"] = "req", ["id"] = id, ["cmd"] = cmd, ["args"] = args ?? new Dictionary<string, object>(),
            }));

            int waitMs = ResponseMarginMs;
            if (args != null && args.TryGetValue("timeoutMs", out object t)) waitMs += Convert.ToInt32(t);
            long deadline = Environment.TickCount64 + waitMs;
            while (true)
            {
                // 생존 신호가 계속 와도 응답이 없으면 끝내야 하므로, 읽기마다 남은 시간을 다시 건다
                long left = deadline - Environment.TickCount64;
                if (left <= 0) throw new IOException("no response to " + cmd + " within " + waitMs + " ms");
                m_Stream.ReadTimeout = (int)left;
                var message = (Dictionary<string, object>)Json.Parse(Frames.Read(m_Stream));
                if ("res".Equals(message["type"]) && Convert.ToInt64(message["id"]) == id) return message;
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
