using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using NUnit.Framework;
using Qaplay;
using Qaplay.Agent;
using UnityEngine;
using UnityEngine.TestTools;

// 러너가 하듯 실제 TCP로 접속해 프레임을 주고받는다. protocol/README.md 1절, 2절, 7절
public class ServerTests
{
    AgentServer m_Server;
    TestClient m_Client;
    readonly List<Dictionary<string, object>> m_Events = new List<Dictionary<string, object>>();

    [SetUp]
    public void StartServer()
    {
        m_Server = AgentServer.Start(0); // 비어 있는 포트를 운영체제가 고른다
        m_Client = new TestClient(m_Server.Port);
        m_Events.Clear();
    }

    [TearDown]
    public void StopServer()
    {
        m_Client.Dispose();
        m_Server.Stop();
    }

    // 응답 하나를 기다린다. 그 사이에 온 이벤트는 m_Events에 모은다
    IEnumerator Receive(Action<Dictionary<string, object>> onResponse, float seconds = 10f)
    {
        float start = Time.realtimeSinceStartup;
        while (Time.realtimeSinceStartup - start < seconds)
        {
            while (m_Client.TryRead(out string json))
            {
                var message = (Dictionary<string, object>)Json.Parse(json);
                if ("evt".Equals(message["type"])) m_Events.Add(message);
                else { onResponse(message); yield break; }
            }
            yield return null;
        }
        Assert.Fail("no response within " + seconds + "s");
    }

    Dictionary<string, object> m_Response;

    IEnumerator Request(string json)
    {
        m_Client.Send(json);
        m_Response = null;
        yield return Receive(r => m_Response = r);
    }

    static Dictionary<string, object> Map(object value) => (Dictionary<string, object>)value;

    const string Hello = "{\"type\":\"req\",\"id\":1,\"cmd\":\"hello\",\"args\":{\"protocol\":{\"min\":1,\"max\":1}}}";

    // 명세 9절의 흐름을 소켓으로 그대로 진행한다
    [UnityTest]
    public IEnumerator Server_DrivesTheSampleShopOverTcp()
    {
        SampleShop.Build();
        yield return null;

        yield return Request(Hello);
        Assert.AreEqual(1.0, m_Response["id"]);
        Assert.AreEqual(true, m_Response["ok"], Json.Write(m_Response));
        Assert.AreEqual(1.0, Map(m_Response["result"])["protocol"]);

        yield return Request("{\"type\":\"req\",\"id\":2,\"cmd\":\"tap\",\"args\":{\"selector\":{\"id\":\"shop.buy\"},\"timeoutMs\":5000}}");
        Assert.AreEqual(true, m_Response["ok"], Json.Write(m_Response));

        yield return Request("{\"type\":\"req\",\"id\":3,\"cmd\":\"wait\",\"args\":{" +
                             "\"selector\":{\"name\":\"GoldLabel\",\"text\":\"1,100\"},\"state\":\"visible\",\"timeoutMs\":5000}}");
        Assert.AreEqual(true, m_Response["ok"], Json.Write(m_Response));

        // 한글이 든 요청과 응답도 그대로 오간다
        yield return Request("{\"type\":\"req\",\"id\":4,\"cmd\":\"find\",\"args\":{\"selector\":{\"text\":\"구매\"}}}");
        Assert.AreEqual("one", Map(m_Response["result"])["resolves"]);

        yield return Request("{\"type\":\"req\",\"id\":5,\"cmd\":\"get\",\"args\":{\"keys\":[\"gold\"]}}");
        Assert.AreEqual(5.0, m_Response["id"]);
        Assert.AreEqual(1100.0, Map(Map(m_Response["result"])["values"])["gold"]);
    }

    // 응답을 기다리지 않고 미리 보낸 요청도 받은 순서대로 처리한다
    [UnityTest]
    public IEnumerator Server_AnswersPipelinedRequestsInOrder()
    {
        m_Client.Send(Hello);
        for (int id = 2; id <= 5; id++)
            m_Client.Send("{\"type\":\"req\",\"id\":" + id + ",\"cmd\":\"get\"}");

        for (int id = 1; id <= 5; id++)
        {
            yield return Receive(r => m_Response = r);
            Assert.AreEqual((double)id, m_Response["id"]);
            Assert.AreEqual(true, m_Response["ok"]);
        }
    }

    [UnityTest]
    public IEnumerator Server_SendsHeartbeatsOnlyAfterHello()
    {
        // hello 전에는 아무것도 오지 않는다
        float start = Time.realtimeSinceStartup;
        while (Time.realtimeSinceStartup - start < 1.3f)
        {
            Assert.IsFalse(m_Client.TryRead(out string early), early);
            yield return null;
        }

        yield return Request(Hello);
        Assert.AreEqual(1000.0, Map(m_Response["result"])["heartbeatMs"]);

        // 2.5초 안에 두 번 이상 오고, 그 사이 프레임 번호가 늘어난다
        start = Time.realtimeSinceStartup;
        while (Time.realtimeSinceStartup - start < 2.5f)
        {
            while (m_Client.TryRead(out string json)) m_Events.Add((Dictionary<string, object>)Json.Parse(json));
            yield return null;
        }
        Assert.GreaterOrEqual(m_Events.Count, 2);
        foreach (Dictionary<string, object> e in m_Events)
        {
            Assert.AreEqual("evt", e["type"]);
            Assert.AreEqual("heartbeat", e["name"]);
        }
        Assert.Greater((double)Map(m_Events[m_Events.Count - 1]["data"])["frame"], (double)Map(m_Events[0]["data"])["frame"]);
    }

    // 게임에서 난 C# 예외는 이벤트로 온다. hello 전의 예외는 보내지 않는다
    [UnityTest]
    public IEnumerator Server_ReportsExceptionsAsEvents()
    {
        using (new QuietExceptions(m_Server, "before hello", "boom"))
            yield return ExceptionsBecomeEvents();
    }

    // 위와 같은 확인을 Unity의 실제 로그를 거쳐서 한다. Console에 빨간 줄이 남으므로 배치 모드에서만 돌린다
    [UnityTest]
    public IEnumerator Server_HearsExceptionsFromUnityLogs()
    {
        if (!Application.isBatchMode) Assert.Ignore("logs real exceptions; runs in batch mode only, to keep the Console clean");
        LogAssert.Expect(LogType.Exception, new System.Text.RegularExpressions.Regex("before hello"));
        LogAssert.Expect(LogType.Exception, new System.Text.RegularExpressions.Regex("boom"));
        yield return ExceptionsBecomeEvents();
    }

    IEnumerator ExceptionsBecomeEvents()
    {
        Debug.LogException(new InvalidOperationException("before hello"));
        yield return Request(Hello);

        Debug.LogException(new InvalidOperationException("boom"));
        // 빈 keys의 get은 아무 값도 읽지 않는다. 러너가 밀린 이벤트를 받아 내는 데 쓴다
        yield return Request("{\"type\":\"req\",\"id\":2,\"cmd\":\"get\",\"args\":{\"keys\":[]}}");
        Assert.AreEqual(0, Map(Map(m_Response["result"])["values"]).Count);

        List<Dictionary<string, object>> exceptions = m_Events.FindAll(e => "exception".Equals(e["name"]));
        Assert.AreEqual(1, exceptions.Count);
        Assert.AreEqual("InvalidOperationException: boom", Map(exceptions[0]["data"])["message"]);
        Assert.IsTrue(Map(exceptions[0]["data"]).ContainsKey("stack"));
    }

    IEnumerator WaitUntilClosed()
    {
        float start = Time.realtimeSinceStartup;
        while (!m_Client.ClosedByServer && Time.realtimeSinceStartup - start < 5f) yield return null;
        Assert.IsTrue(m_Client.ClosedByServer, "서버가 연결을 끊어야 한다");
    }

    // 응답할 수 없는 프레임과 너무 큰 프레임은 연결을 끊는다. 서버는 그 뒤에도 새 접속을 받는다
    [UnityTest]
    public IEnumerator Server_ClosesTheConnectionOnBadFramesAndAcceptsTheNextOne()
    {
        m_Client.Send("this is not json");
        yield return WaitUntilClosed();

        m_Client.Dispose();
        m_Client = new TestClient(m_Server.Port);
        m_Client.SendRaw(new byte[] { 0x7f, 0xff, 0xff, 0xff }); // 길이가 16 MiB보다 크다
        yield return WaitUntilClosed();

        m_Client.Dispose();
        m_Client = new TestClient(m_Server.Port);
        yield return Request(Hello);
        Assert.AreEqual(true, m_Response["ok"]);
    }

    // 메인 스레드를 막지 않는 최소한의 클라이언트: 쌓인 바이트에서 완성된 프레임만 꺼낸다
    sealed class TestClient : IDisposable
    {
        readonly TcpClient m_Tcp;
        readonly NetworkStream m_Stream;
        readonly MemoryStream m_Buffer = new MemoryStream();
        bool m_Eof;

        public TestClient(int port)
        {
            m_Tcp = new TcpClient("127.0.0.1", port) { NoDelay = true };
            m_Stream = m_Tcp.GetStream();
        }

        public void Send(string json)
        {
            byte[] body = Encoding.UTF8.GetBytes(json);
            SendRaw(new[] { (byte)(body.Length >> 24), (byte)(body.Length >> 16), (byte)(body.Length >> 8), (byte)body.Length });
            SendRaw(body);
        }

        public void SendRaw(byte[] bytes) => m_Stream.Write(bytes, 0, bytes.Length);

        void Pump()
        {
            var chunk = new byte[4096];
            while (!m_Eof && m_Tcp.Client.Poll(0, SelectMode.SelectRead))
            {
                int n;
                try { n = m_Stream.Read(chunk, 0, chunk.Length); }
                catch (IOException) { n = 0; }
                if (n <= 0) m_Eof = true;
                else m_Buffer.Write(chunk, 0, n);
            }
        }

        public bool ClosedByServer
        {
            get { Pump(); return m_Eof; }
        }

        public bool TryRead(out string json)
        {
            Pump();
            json = null;
            byte[] data = m_Buffer.GetBuffer();
            long size = m_Buffer.Length;
            if (size < 4) return false;
            int length = (data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3];
            if (size < 4 + length) return false;
            json = Encoding.UTF8.GetString(data, 4, length);

            // 꺼낸 프레임을 버리고 나머지를 앞으로 당긴다
            var rest = new byte[size - 4 - length];
            Buffer.BlockCopy(data, 4 + length, rest, 0, rest.Length);
            m_Buffer.SetLength(0);
            m_Buffer.Write(rest, 0, rest.Length);
            return true;
        }

        public void Dispose() => m_Tcp.Close();
    }
}
