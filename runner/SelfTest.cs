using System;
using System.Collections.Generic;
using System.IO;

namespace Qaplay.Runner
{
    /// <summary>
    /// 게임 없이 확인할 수 있는 러너의 로직을 검사한다: qaplay selftest.
    /// 게임을 거치는 전체 경로는 Unity의 PlayMode 테스트(RunnerTests)가 확인한다.
    /// </summary>
    static class SelfTest
    {
        public static int Run()
        {
            Frame();
            Arguments();
            Retries();
            NoRerunForBrokenGames();
            LostGames();
            AdbDevices();
            JunitXml();
            Console.WriteLine("selftest ok");
            return 0;
        }

        static void Check(bool condition, string what)
        {
            if (!condition) throw new InvalidOperationException("selftest failed: " + what);
        }

        static void Frame()
        {
            var stream = new MemoryStream();
            Frames.Write(stream, "{\"type\":\"req\",\"id\":1,\"cmd\":\"dump\"}");
            Frames.Write(stream, "{\"t\":\"구매\"}");
            byte[] bytes = stream.ToArray();
            Check(bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0 && bytes[3] == 0x22, "length prefix of the spec example is 00 00 00 22");
            stream.Position = 0;
            Check(Frames.Read(stream) == "{\"type\":\"req\",\"id\":1,\"cmd\":\"dump\"}", "first frame round-trips");
            Check(Frames.Read(stream) == "{\"t\":\"구매\"}", "length counts UTF-8 bytes, not characters");
            try
            {
                Frames.Read(new MemoryStream(new byte[] { 0x7f, 0xff, 0xff, 0xff }));
                Check(false, "an oversized frame is refused");
            }
            catch (IOException) { }
        }

        static void Arguments()
        {
            var args = new Args(new[] { "serve", "a.yaml", "--port", "5", "b", "--", "python", "--port", "x" });
            Check(args.Command == "serve" && args.Paths.Count == 2 && args.Int("port", 1) == 5, "options and paths");
            Check(args.Rest.Count == 3 && args.Rest[1] == "--port", "everything after -- is the command");
            Check(args.Int("retries", 1) == 1 && args.Get("junit", null) == null, "defaults");
            foreach (string[] bad in new[] { new[] { "run", "--nope", "1" }, new[] { "run", "--port" } })
            {
                try { new Args(bad); Check(false, "bad options are refused"); }
                catch (UsageException) { }
            }
            try { new Args(new[] { "run", "--retries", "-1" }).Int("retries", 1); Check(false, "negative numbers are refused"); }
            catch (UsageException) { }
        }

        static void Retries()
        {
            var step = new RuleStep { line = 1, source = "- tapOn: x" };
            RuleResult Fail() => new RuleResult { index = 1, step = step, code = "not_found", message = "m" };
            RuleResult Pass() => new RuleResult { passed = true };

            int calls = 0;
            var passed = new Outcome();
            passed.RunWithRetries(1, _ => { calls++; return Pass(); });
            Check(passed.Status == "passed" && calls == 1, "a passing rule runs once");

            calls = 0;
            var flaky = new Outcome();
            flaky.RunWithRetries(1, _ => ++calls == 1 ? Fail() : Pass());
            Check(flaky.Status == "flaky" && calls == 2 && flaky.failures.Count == 1, "passing on the rerun is flaky");

            calls = 0;
            var failed = new Outcome();
            failed.RunWithRetries(1, _ => { calls++; return Fail(); });
            Check(failed.Status == "failed" && calls == 2 && failed.failureCode == "not_found", "failing every time is failed");

            calls = 0;
            var once = new Outcome();
            once.RunWithRetries(0, _ => { calls++; return Fail(); });
            Check(once.Status == "failed" && calls == 1, "--retries 0 runs once");

            var lost = new Outcome();
            lost.RunWithRetries(0, _ => new RuleResult { code = "connection_lost", message = "gone" });
            Check(lost.failures[0] == "connection_lost: gone", "a failure without a command is still reported");
        }

        // 예외, 크래시, 프리징은 다시 돌리지 않는다
        static void NoRerunForBrokenGames()
        {
            foreach (string code in new[] { "exception", "crash", "freeze" })
            {
                int calls = 0;
                var outcome = new Outcome();
                outcome.RunWithRetries(3, attempt =>
                {
                    calls++;
                    return new RuleResult { code = code, message = "m" };
                });
                Check(outcome.Status == "failed" && calls == 1 && outcome.failureCode == code, code + " is not rerun");
            }

            var attempts = new List<int>();
            var twice = new Outcome();
            twice.RunWithRetries(1, attempt =>
            {
                attempts.Add(attempt);
                return new RuleResult { code = "timeout", message = "m" };
            });
            Check(attempts.Count == 2 && attempts[0] == 0 && attempts[1] == 1, "the attempt number is passed along");
        }

        // 가짜 게임: hello에는 답하고, 그다음 요청에는 mode에 따라 행동한다
        static int FakeGame(string mode)
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            new System.Threading.Thread(() =>
            {
                try
                {
                    using System.Net.Sockets.TcpClient client = listener.AcceptTcpClient();
                    listener.Stop();
                    Stream stream = client.GetStream();
                    Frames.Read(stream);
                    Frames.Write(stream, "{\"type\":\"res\",\"id\":1,\"ok\":true,\"result\":{}}");
                    Frames.Read(stream);
                    const string answer = "{\"type\":\"res\",\"id\":2,\"ok\":true,\"result\":{}}";
                    switch (mode)
                    {
                        case "crash": // 답하지 않고 연결을 끊는다
                            return;
                        case "exception":
                            Frames.Write(stream, "{\"type\":\"evt\",\"name\":\"exception\",\"data\":{\"message\":\"Boom: x\",\"stack\":\"at A.B ()\\n\"}}");
                            Frames.Write(stream, "{\"type\":\"evt\",\"name\":\"something-new\",\"data\":{}}");
                            Frames.Write(stream, answer);
                            break;
                        case "freeze": // 살아 있다는 신호는 오지만 프레임 번호가 그대로다
                            while (true)
                            {
                                Frames.Write(stream, "{\"type\":\"evt\",\"name\":\"heartbeat\",\"data\":{\"frame\":7}}");
                                System.Threading.Thread.Sleep(100);
                            }
                        case "slow": // 오래 걸리지만 프레임은 계속 늘어난다
                            for (int frame = 0; frame < 12; frame++)
                            {
                                Frames.Write(stream, "{\"type\":\"evt\",\"name\":\"heartbeat\",\"data\":{\"frame\":" + frame + "}}");
                                System.Threading.Thread.Sleep(100);
                            }
                            Frames.Write(stream, answer);
                            break;
                    }
                    Frames.Read(stream); // 상대가 끊을 때까지 기다린다
                }
                catch (IOException)
                {
                    // 상대가 끊었다
                }
            }) { IsBackground = true }.Start();
            return port;
        }

        static void LostGames()
        {
            using (var client = new AgentClient("127.0.0.1", FakeGame("exception"), 500))
            {
                Check(true.Equals(client.Request("get", null)["ok"]), "a response still arrives after events");
                Check(client.Exceptions.Count == 1 && client.Exceptions[0] == "Boom: x\nat A.B ()", "exceptions are collected; unknown events are ignored");
            }

            using (var client = new AgentClient("127.0.0.1", FakeGame("slow"), 500))
                Check(true.Equals(client.Request("get", null)["ok"]), "a slow answer is not a freeze while frames advance");

            foreach (string kind in new[] { "crash", "freeze" })
            {
                using var client = new AgentClient("127.0.0.1", FakeGame(kind), 500);
                var watch = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    client.Request("tap", null);
                    Check(false, kind + " must not return a response");
                }
                catch (AgentLostException e)
                {
                    Check(e.Kind == kind, "a " + kind + " is told apart, got " + e.Kind + ": " + e.Message);
                    Check(watch.ElapsedMilliseconds < 3000, kind + " is noticed quickly");
                }
            }
        }

        static void AdbDevices()
        {
            List<Adb.Device> devices = Adb.Parse(
                "* daemon not running; starting now at tcp:5037\r\n* daemon started successfully\r\n" +
                "List of devices attached\r\n" +
                "R58M123ABC             device product:o1sks model:SM_G991N device:o1s transport_id:1\r\n" +
                "emulator-5554          offline\r\n\r\n");
            Check(devices.Count == 2, "two devices");
            Check(devices[0].serial == "R58M123ABC" && devices[0].state == "device" && devices[0].model == "SM_G991N", "serial, state, model");
            Check(devices[1].serial == "emulator-5554" && devices[1].state == "offline" && devices[1].model == "", "a device without details");
            Check(Adb.Parse("List of devices attached\n\n").Count == 0, "no devices");
        }

        static void JunitXml()
        {
            var info = (Dictionary<string, object>)Json.Parse(
                "{\"app\":{\"id\":\"com.example\",\"version\":\"1.0\"},\"platform\":\"Android\",\"unity\":\"6000.3\",\"screen\":{\"width\":1080,\"height\":2400}}");
            var passed = new Outcome { file = "a.yaml", passed = true, rule = new Rule { tc = "TC-1", name = "상점 <구매>" } };
            var flaky = new Outcome { file = "b.yaml", passed = true };
            flaky.Fail("timeout", "attempt & one");
            var failed = new Outcome { file = "c.yaml" };
            failed.Fail("not_found", "no element");

            string path = Path.Combine(Path.GetTempPath(), "qaplay-selftest-" + Guid.NewGuid().ToString("N"), "out.xml");
            Junit.Write(path, new List<Outcome> { passed, flaky, failed }, info, 1.5);
            string xml = File.ReadAllText(path);
            Directory.Delete(Path.GetDirectoryName(path), true);

            Check(xml.Contains("tests=\"3\"") && xml.Contains("failures=\"1\""), "flaky counts as passed");
            Check(xml.Contains("classname=\"TC-1\"") && xml.Contains("name=\"상점 &lt;구매&gt;\""), "tc and name, escaped");
            Check(xml.Contains("<flakyFailure type=\"timeout\" message=\"attempt &amp; one\""), "flaky attempts are kept");
            Check(xml.Contains("<failure type=\"not_found\" message=\"no element\">"), "failure");
            Check(xml.Contains("name=\"screen\" value=\"1080x2400\"") && xml.Contains("name=\"platform\" value=\"Android\""), "where it ran");
        }
    }
}
