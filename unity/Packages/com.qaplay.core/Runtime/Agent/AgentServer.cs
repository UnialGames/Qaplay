using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

namespace Qaplay.Agent
{
    /// <summary>
    /// 러너가 접속하는 TCP 서버. 프레임(4바이트 길이 + JSON)을 받아 AgentSession에 넘기고 응답을 돌려보낸다.
    /// protocol/README.md 1절, 2절, 7절. Play를 시작하면(에디터와 QA 빌드) 자동으로 열린다.
    /// </summary>
    // ponytail: 이 컴퓨터 안(127.0.0.1)에서 오는 접속만 받고 토큰은 검사하지 않는다.
    // Wi-Fi 접속을 열 때 토큰 검사(unauthorized)를 함께 넣는다.
    // 연결은 한 번에 하나만 받는다. 두 번째 접속은 첫 연결이 끊길 때까지 기다린다.
    public sealed class AgentServer : MonoBehaviour
    {
        public const int DefaultPort = 27275;
        const int MaxFrameBytes = 16 * 1024 * 1024;

        /// <summary>실제로 열린 포트. Start에 0을 넘기면 운영체제가 고른 번호다.</summary>
        public int Port { get; private set; }

        TcpListener m_Listener;
        Thread m_AcceptThread;
        volatile bool m_Stopping;
        volatile Connection m_Connection;
        readonly ConcurrentQueue<(Connection connection, string json)> m_Requests =
            new ConcurrentQueue<(Connection, string)>();
        bool m_Busy;
        int m_Frame;

        /// <summary>서버를 연다. 포트를 열 수 없으면 SocketException.</summary>
        public static AgentServer Start(int port)
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();

            // Play가 끝나면 함께 사라지도록 숨기기만 한다
            var go = new GameObject("qaplay server") { hideFlags = HideFlags.HideInHierarchy };
            DontDestroyOnLoad(go);
            var server = go.AddComponent<AgentServer>();
            server.m_Listener = listener;
            server.Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            server.m_AcceptThread = new Thread(server.AcceptLoop) { IsBackground = true, Name = "qaplay accept" };
            server.m_AcceptThread.Start();
            return server;
        }

        public void Stop()
        {
            if (m_Stopping) return;
            m_Stopping = true;
            m_Listener.Stop();
            m_Connection?.Close();
            if (this != null) Destroy(gameObject);
        }

        void OnDestroy() => Stop();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AutoStart()
        {
            int port = ConfiguredPort();
            try
            {
                Start(port);
                Debug.Log("[qaplay] agent listening on 127.0.0.1:" + port);
            }
            catch (SocketException e)
            {
                // 같은 포트를 다른 에디터나 게임이 이미 쓰는 경우다. 게임은 그대로 돌게 둔다
                Debug.LogWarning("[qaplay] agent could not listen on port " + port + ": " + e.Message);
            }
        }

        // 포트를 바꾸는 방법: 실행 인자 -qaplay-port N, 그다음 환경 변수 QAPLAY_PORT
        static int ConfiguredPort()
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++)
                if (args[i] == "-qaplay-port" && int.TryParse(args[i + 1], out int fromArgs))
                    return fromArgs;
            return int.TryParse(Environment.GetEnvironmentVariable("QAPLAY_PORT"), out int fromEnv) ? fromEnv : DefaultPort;
        }

        // ---- 받는 쪽: 별도 스레드 ----

        void AcceptLoop()
        {
            while (!m_Stopping)
            {
                TcpClient client;
                try
                {
                    client = m_Listener.AcceptTcpClient();
                }
                catch (Exception)
                {
                    return; // 서버를 닫았다
                }

                var connection = new Connection(client);
                m_Connection = connection;
                try
                {
                    ReadFrames(connection);
                }
                catch (Exception)
                {
                    // 상대가 끊었거나 우리가 닫았다
                }
                connection.Close();
                m_Connection = null;
            }
        }

        void ReadFrames(Connection connection)
        {
            var header = new byte[4];
            while (true)
            {
                connection.ReadExactly(header, 4);
                long length = ((long)header[0] << 24) | ((long)header[1] << 16) | ((long)header[2] << 8) | header[3];
                if (length > MaxFrameBytes) return; // 본문을 읽지 않고 끊는다
                var body = new byte[length];
                connection.ReadExactly(body, (int)length);
                m_Requests.Enqueue((connection, Encoding.UTF8.GetString(body)));
            }
        }

        // ---- 처리하는 쪽: 메인 스레드 ----

        void Update()
        {
            Volatile.Write(ref m_Frame, Time.frameCount);
            // 요청은 받은 순서대로 하나씩 처리한다
            if (m_Busy || !m_Requests.TryDequeue(out (Connection connection, string json) request)) return;
            if (!request.connection.Closed) StartCoroutine(Process(request.connection, request.json));
        }

        IEnumerator Process(Connection connection, string json)
        {
            IEnumerator work;
            try
            {
                work = connection.session.Handle(json, connection.Send);
            }
            catch (FormatException)
            {
                connection.Close(); // 응답할 수 없는 프레임
                yield break;
            }

            m_Busy = true;
            try
            {
                while (work.MoveNext()) yield return work.Current;
            }
            finally
            {
                m_Busy = false;
            }
            // 생존 신호는 hello 응답을 보낸 뒤부터 보낸다
            if (connection.session.HelloDone) connection.StartHeartbeat(() => Volatile.Read(ref m_Frame));
        }

        sealed class Connection
        {
            public readonly AgentSession session = new AgentSession();
            readonly TcpClient m_Client;
            readonly NetworkStream m_Stream;
            readonly object m_WriteLock = new object();
            Timer m_Heartbeat;
            volatile bool m_Closed;

            public bool Closed => m_Closed;

            public Connection(TcpClient client)
            {
                m_Client = client;
                m_Client.NoDelay = true;
                m_Stream = client.GetStream();
            }

            public void ReadExactly(byte[] buffer, int count)
            {
                for (int read = 0; read < count;)
                {
                    int n = m_Stream.Read(buffer, read, count - read);
                    if (n <= 0) throw new EndOfStreamException();
                    read += n;
                }
            }

            // 응답은 메인 스레드에서, 생존 신호는 타이머 스레드에서 보낸다. 프레임이 섞이지 않게 한 번에 하나만 쓴다
            public void Send(string json)
            {
                byte[] body = Encoding.UTF8.GetBytes(json);
                var frame = new byte[4 + body.Length];
                frame[0] = (byte)(body.Length >> 24);
                frame[1] = (byte)(body.Length >> 16);
                frame[2] = (byte)(body.Length >> 8);
                frame[3] = (byte)body.Length;
                Buffer.BlockCopy(body, 0, frame, 4, body.Length);
                lock (m_WriteLock)
                {
                    if (m_Closed) return;
                    try
                    {
                        m_Stream.Write(frame, 0, frame.Length);
                    }
                    catch (Exception)
                    {
                        Close(); // 상대가 끊었다
                    }
                }
            }

            // 메인 스레드가 멈춰도 계속 보내도록 타이머 스레드에서 보낸다. 그때는 frame 값만 멈춘다 (명세 7절)
            public void StartHeartbeat(Func<int> frame)
            {
                if (m_Heartbeat != null || m_Closed) return;
                m_Heartbeat = new Timer(
                    _ => Send("{\"type\":\"evt\",\"name\":\"heartbeat\",\"data\":{\"frame\":" + frame() + "}}"),
                    null, AgentSession.HeartbeatMs, AgentSession.HeartbeatMs);
            }

            public void Close()
            {
                m_Closed = true;
                m_Heartbeat?.Dispose();
                m_Client.Close();
            }
        }

        sealed class EndOfStreamException : Exception { }
    }
}
