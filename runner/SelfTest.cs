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
            Bugs();
            AdbDevices();
            JunitXml();
            LeakTrend();
            DeviceOutput();
            Exceptions();
            Console.WriteLine("selftest ok");
            return 0;
        }

        // Android 셸이 돌려준 글에서 기기 지표를 읽는다. 글은 에뮬레이터(Android 17)에서 받은 것을 줄인 것이다
        static void DeviceOutput()
        {
            const string output =
                "TCK\r\n100\r\nUPTIME\r\n30.77 29.26\r\nSTAT\r\n" +
                "2549 (m.qaplay (x) y) S 461 461 0 0 -1 4194624 33443 0 42 0 51 93 0 0 10 -10 65 0 2455 21193195520 71133\r\n" +
                "Thermal Status: 2\r\n" +
                "Current Battery Service state:\r\n  AC powered: false\r\n  level: 87\r\n  scale: 100\r\n  temperature: 315\r\n  Capacity level: -1\r\n" +
                "           TOTAL PSS:   152360            TOTAL RSS:   285960      TOTAL SWAP (KB):        0\r\n";
            DeviceMeter.Sample sample = DeviceMeter.Parse(output);
            Check(sample.cpuSeconds == 1.44 && sample.clock == 30.77, "cpu time is utime + stime in seconds, even with ')' in the name");
            Check(sample.thermal == 2 && sample.battery == 87 && sample.batteryTemp == 31.5, "thermal status, battery level and temperature");
            Check(sample.pss == Math.Round(152360 / 1024.0, 1), "PSS in MB");

            // 앱이 떠 있지 않으면 stat을 읽지 못한다. 나머지는 그대로 읽는다
            DeviceMeter.Sample noApp = DeviceMeter.Parse("TCK\n100\nUPTIME\n31.00 29.00\nSTAT\nThermal Status: 0\n");
            Check(noApp.cpuSeconds == null && noApp.pss == null && noApp.thermal == 0, "missing values stay null");
            Check(DeviceMeter.Summary(new Dictionary<string, object> { ["cpu"] = null }).StartsWith("not available"), "nothing to show is said so");
        }

        // 배치 검사의 예외 목록: 등록한 문제만 빠지고, 파일로 남겼다가 다시 읽어도 같다
        static void Exceptions()
        {
            string file = Path.Combine(Path.GetTempPath(), "qaplay-selftest-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                Dictionary<string, object> Issue(string kind, string path, string other = null)
                {
                    var issue = new Dictionary<string, object> { ["kind"] = kind, ["path"] = path, ["element"] = new Dictionary<string, object>() };
                    if (other != null) issue["otherPath"] = other;
                    return issue;
                }
                var args = new Args(new[] { "layout", "--exceptions", file });
                LayoutExceptions exceptions = LayoutExceptions.Load(args);
                Check(exceptions.Items.Count == 0, "a missing file is an empty list");

                exceptions.Add(Issue("overlap", "Canvas/A", "Canvas/B"), "의도한 겹침");
                exceptions.Add(Issue("overflow", "Canvas/Title"), "말줄임");
                exceptions.Add(Issue("overflow", "Canvas/Title"), "말줄임으로 자르기로 함");
                Check(exceptions.Items.Count == 2 && (string)exceptions.Items[1]["reason"] == "말줄임으로 자르기로 함", "adding the same issue again only changes the reason");
                Check(!exceptions.Items[0].ContainsKey("element"), "only what identifies the issue is kept");
                exceptions.Save();

                LayoutExceptions loaded = LayoutExceptions.Load(args);
                Check(loaded.Items.Count == 2, "saved exceptions are read back");
                Check(File.ReadAllText(file).Split('\n').Length == 5, "one exception per line");
                var result = new Dictionary<string, object>
                {
                    ["issues"] = new List<object>
                    {
                        Issue("overlap", "Canvas/B", "Canvas/A"),   // 순서만 바뀐 같은 겹침
                        Issue("overlap", "Canvas/A", "Canvas/C"),   // 다른 상대
                        Issue("overflow", "Canvas/Title"),
                        Issue("overlap", "Canvas/Title", "Canvas/A"), // 같은 요소지만 종류가 다르다
                    },
                };
                Check(loaded.Filter(result) == 2 && ((List<object>)result["issues"]).Count == 2, "only the registered issues are skipped");

                File.WriteAllText(file, "[ {\"kind\": \"overlap\" } ]");
                try { LayoutExceptions.Load(args); Check(false, "a broken exception file is refused"); }
                catch (UsageException) { }
            }
            finally
            {
                File.Delete(file);
            }
        }

        // 누수 판정의 추세 계산: 계속 느는 것만 잡고, 처음에만 늘다 멈춘 것과 한 번 튄 값에는 넘어가지 않는다
        static void LeakTrend()
        {
            var leaking = new List<double>();
            var warmingUp = new List<double>();
            var spike = new List<double>();
            for (int n = 0; n < 20; n++)
            {
                leaking.Add(50 + n);                  // 되풀이마다 1MB
                warmingUp.Add(n < 8 ? 50 + n : 58);   // 풀이 차는 8번째까지만 늘고 멈춘다
                spike.Add(n == 15 ? 53 : 50);         // 한 번만 3MB 튄다
            }
            Check(Math.Abs(RuleRunner.GrowthPerRepeat(leaking) - 1) < 1e-9, "a steady leak is measured per repeat");
            Check(Math.Abs(RuleRunner.GrowthPerRepeat(warmingUp)) < 1e-9, "growth that stops in the first half is not a leak");
            Check(Math.Abs(RuleRunner.GrowthPerRepeat(spike)) < 0.05, "one spike does not look like a leak");
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
            Check(args.Int("retries", 1) == 1 && args.Get("junit", null) == null && !args.Json, "defaults");
            var json = new Args(new[] { "dump", "--json", "--port", "5" });
            Check(json.Json && json.Paths.Count == 0 && json.Int("port", 1) == 5, "--json takes no value");
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

        static void Bugs()
        {
            Check(BugStore.NormalizeId("3") == "0003" && BugStore.NormalizeId("0003") == "0003" && BugStore.NormalizeId("bug-12") == "0012", "bug ids");
            try { BugStore.NormalizeId("x"); Check(false, "bad ids are refused"); }
            catch (UsageException) { }

            // 같은 예외는 빌드가 달라 파일 위치가 달라도 같은 버그다. 호출 지점이 다르면 다른 버그다
            string first = "InvalidOperationException: boom\nShop.Buy () (at <aaa>:0)\nUI.Click ()";
            string again = "InvalidOperationException: boom again\nShop.Buy () (at Assets/Shop.cs:12)\nUI.Click ()";
            string other = "InvalidOperationException: boom\nShop.Sell () (at <aaa>:0)";
            Check(BugStore.Fingerprint("exception", "r", first) == BugStore.Fingerprint("exception", "other-rule", again), "same exception, same bug");
            Check(BugStore.Fingerprint("exception", "r", first) != BugStore.Fingerprint("exception", "r", other), "another call site, another bug");
            Check(BugStore.Fingerprint("crash", "shop", null) == "crash|shop" && BugStore.Fingerprint("freeze", "shop", null) != "crash|shop", "crashes and freezes go by rule");

            string root = Path.Combine(Path.GetTempPath(), "qaplay-selftest-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(root);
                string rule = Path.Combine(root, "shop.yaml");
                File.WriteAllText(rule, "- tapOn: 구매\n");
                var store = new BugStore(Path.Combine(root, "bugs"));

                Outcome Failed(string code, string evidence)
                {
                    var outcome = new Outcome { file = rule, evidence = evidence };
                    outcome.Fail(code, code + ": details");
                    return outcome;
                }

                Check(store.RecordAuto(Failed("timeout", null), null) == null, "ordinary failures are not registered");
                Check(store.RecordAuto(new Outcome { file = rule, passed = true }, null) == null, "passing rules are not registered");
                Check(store.RecordAuto(Failed("exception", first), null) == "bug 0001 registered", "a new bug");
                Check(store.RecordAuto(Failed("exception", again), null) == "bug 0001 seen 2 times", "the same bug again");
                Check(store.RecordAuto(Failed("crash", null), null) == "bug 0002 registered", "another bug gets the next number");

                Dictionary<string, object> bug = store.Load("0001");
                Check("open".Equals(bug["status"]) && "major".Equals(bug["severity"]) && Convert.ToInt32(bug["count"]) == 2, "status, severity, count");
                Check("critical".Equals(store.Load("0002")["severity"]), "crashes are critical");
                Check(File.ReadAllText(Path.Combine(store.FolderOf("0001"), "steps.yaml")).Contains("구매"), "the rule is kept as the steps");
                Check(File.ReadAllText(Path.Combine(store.FolderOf("0001"), "bug.json")).Contains("\n  \"title\""), "bug.json is written for people to read");

                // 고쳤다고 확인한 버그가 다시 나면 다시 열리고 회귀로 표시된다
                bug["status"] = "verified";
                store.Save(bug);
                Check(store.RecordAuto(Failed("exception", first), null) == "bug 0001 seen 3 times, reopened as a regression", "regression");
                bug = store.Load("0001");
                Check("open".Equals(bug["status"]) && true.Equals(bug["regression"]), "reopened");
                Check(store.All().Count == 2, "two bugs in all");
            }
            finally
            {
                Directory.Delete(root, true);
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

            // 앱이 요청한 권한: "dumpsys package"의 한 구역만 읽는다. 글은 에뮬레이터(Android 17)에서 받은 것이다
            List<string> permissions = Adb.RequestedPermissions(
                "    versionName=0.1.0\r\n    requested permissions:\r\n      android.permission.INTERNET\r\n      android.permission.CAMERA\r\n" +
                "      android.permission.WRITE_EXTERNAL_STORAGE: restricted=true\r\n" +
                "    install permissions:\r\n      android.permission.INTERNET: granted=true\r\n    User 0: installed=true\r\n" +
                "      runtime permissions:\r\n        android.permission.CAMERA: granted=false, flags=[ ]\r\n");
            Check(permissions.Count == 3 && permissions[1] == "android.permission.CAMERA" && permissions[2] == "android.permission.WRITE_EXTERNAL_STORAGE",
                "requested permissions end where the next section starts");
            Check(Adb.RequestedPermissions("Unable to find package: com.nope\n").Count == 0, "an app that is not installed has none");
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
