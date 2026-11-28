using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

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
            Sheets();
            ExcelSheets();
            Reports();
            DashboardServer();
            CiSummary();
            GoogleSelfTest.Run();
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

        // 시트: CSV를 그대로 읽고 쓰는지, 도구의 칸만 고치는지, 규칙 상태와 요약 줄
        static void Sheets()
        {
            // 쉼표, 따옴표, 줄바꿈, 빈 칸이 든 표가 읽고 쓴 뒤에도 같다
            var table = new List<List<string>>
            {
                new List<string> { "TC", "내용", "기대 결과" },
                new List<string> { "TC-1", "\"구매\"를 누른다, 두 번", "골드가\n줄어든다" },
                new List<string> { "TC-2", "", "" },
            };
            List<List<string>> again = Csv.Parse(Csv.Write(table));
            Check(again.Count == 3 && again[1][1] == table[1][1] && again[1][2] == table[1][2] && again[2].Count == 3, "CSV survives a round trip");
            Check(Csv.Parse("a,b\r\n\r\n1,2\n").Count == 2, "blank lines are not rows");

            string folder = Path.Combine(Path.GetTempPath(), "qaplay-selftest-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(folder, "rules"));
                File.WriteAllText(Path.Combine(folder, "rules", "buy.yaml"), "tc: TC-1\n---\n- tapOn: 구매\n");
                File.WriteAllText(Path.Combine(folder, "rules", "gift.yaml"), "tc: TC-9\n---\n- tapOn: 선물\n");
                string tcFile = Path.Combine(folder, "tc.csv"), settings = Path.Combine(folder, "sheet.json");
                // 사람이 쓰던 양식: 도구의 칸이 아직 없고, 뒤의 빈 칸을 적지 않은 줄도 있다
                File.WriteAllText(tcFile, "번호,내용,기대 결과,담당\r\nTC-1,구매를 누른다,골드가 100 준다,철수\r\nTC-2,판매를 누른다\r\n");
                File.WriteAllText(settings,
                    "{\"tc\":\"tc.csv\",\"summary\":\"summary.csv\",\"rules\":[\"rules\"]," +
                    "\"columns\":{\"tc\":\"번호\",\"content\":\"내용\",\"expected\":\"기대 결과\",\"result\":\"결과\"},\"labels\":{\"passed\":\"통과\",\"failed\":\"실패\"}}");
                var args = new Args(new[] { "run", "--sheet", settings });
                SheetConfig config = SheetConfig.Load(args);

                Outcome Ran(string tc, bool passed)
                {
                    var outcome = new Outcome { file = tc + ".yaml", rule = RuleReader.Read("tc: " + tc + "\n---\n- tapOn: a\n"), passed = passed };
                    if (!passed) outcome.Fail("not_found", "x");
                    return outcome;
                }
                var first = new Outcome { file = "buy.yaml", rule = RuleReader.Read("tc: TC-1\n---\n- tapOn: a\n"), passed = false, bugId = "0003" };
                first.Fail("crash", "x");
                List<string> notes = new TcSheet(config).Record(new List<Outcome> { first, Ran("TC-9", true) }, "com.game 1.2", "Android 1080x2400");

                List<List<string>> rows = Csv.Parse(File.ReadAllText(tcFile));
                List<string> header = rows[0], one = rows[1], two = rows[2];
                Check(header.Count == 10 && header[3] == "담당" && header[4] == "Rule" && header[5] == "결과", "tool columns are added after the human ones");
                Check(one[1] == "구매를 누른다" && one[3] == "철수", "human cells are untouched");
                Check(one[header.IndexOf("Rule")] == "ok" && one[header.IndexOf("결과")] == "실패", "rule status and latest result of a TC that ran");
                Check(one[header.IndexOf("Last run")].StartsWith("com.game 1.2 / Android 1080x2400 / "), "build, device and time of the latest run");
                Check(one[header.IndexOf("Bugs")] == "BUG-0003", "the bug registered for the failure is linked");
                Check(two[header.IndexOf("Rule")] == "missing" && two[header.IndexOf("결과")] == "", "a TC without a rule is marked, its result left alone");
                Check(notes.Exists(n => n.Contains("TC-9 is not in the sheet")), "a rule whose TC is not in the sheet is reported");

                List<List<string>> summary = Csv.Parse(File.ReadAllText(Path.Combine(folder, "summary.csv")));
                Check(summary.Count == 2 && summary[0][0] == "Build" && string.Join("|", summary[1].GetRange(3, 5)) == "2|1|1|0|50%", "a summary row per run");

                // 같은 버그가 다시 올라와도 번호를 두 번 적지 않는다. 요약에는 줄이 하나 더 는다
                new TcSheet(config).Record(new List<Outcome> { first }, "com.game 1.3", "Android");
                rows = Csv.Parse(File.ReadAllText(tcFile));
                Check(rows[1][header.IndexOf("Bugs")] == "BUG-0003" && Csv.Parse(File.ReadAllText(Path.Combine(folder, "summary.csv"))).Count == 3, "bugs are not listed twice");

                // 사람이 기대 결과를 고치면 규칙을 다시 봐야 한다. 규칙을 고친 뒤 accept하면 풀린다
                rows[1][2] = "골드가 200 준다";
                File.WriteAllText(tcFile, Csv.Write(rows));
                var sheet = new TcSheet(config);
                Check(sheet.List().Find(t => t.id == "TC-1").ruleStatus == "outdated", "a changed expectation makes the rule outdated");
                sheet.Record(null, null, null);
                Check(Csv.Parse(File.ReadAllText(tcFile))[1][header.IndexOf("Rule")] == "outdated", "sync writes the status");
                sheet.Record(null, null, null, new[] { "TC-1" });
                Check(sheet.List().Find(t => t.id == "TC-1").ruleStatus == "ok", "accept clears it");

                File.WriteAllText(settings, "{\"tc\":\"tc.csv\",\"columns\":{\"reslt\":\"결과\"}}");
                try { SheetConfig.Load(args); Check(false, "a misspelled column name is refused"); }
                catch (UsageException) { }
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }

        // Excel 시트: 탭이 둘인 파일 하나. 사람이 꾸민 서식과 다른 탭이 그대로 남는지가 CSV와 다른 점이다
        // GitHub Actions의 작업 요약: 규칙마다 한 줄, 실패 내용은 코드 블록 안에 가둔다
        static void CiSummary()
        {
            var passed = new Outcome { file = "rules/shop.yaml", rule = RuleReader.Read("name: 상점 구매\ntc: TC-012\n---\n- tapOn: a\n"), passed = true, seconds = 0.84 };
            var failed = new Outcome { file = "rules/a|b.yaml", rule = RuleReader.Read("- tapOn: a\n"), bugId = "0003" };
            failed.Fail("exception", "exception: <img src=x> ``` | done");
            string md = GithubActions.Summary(new List<Outcome> { passed, failed }, "com.game 1.2", "Android `x`");
            Check(md.StartsWith("### qaplay: 1 passed, 0 flaky, 1 failed\n"), "the summary starts with the counts");
            Check(md.Contains("`com.game 1.2` on `Android 'x'`"), "the build and the device are named");
            Check(md.Contains("| ✅ passed | 상점 구매 (shop.yaml) | TC-012 |  | 0.8s |"), "a row per rule");
            Check(md.Contains("| ❌ failed | a\\|b.yaml |  | BUG-0003 | 0.0s |"), "a table cell cannot break the table");
            Check(md.Contains("```text\nattempt 1: exception: <img src=x> ''' | done\n```"), "failure text stays inside its code block");

            // Actions 밖에서는 아무것도 쓰지 않고, 안에서는 요약과 리포트 경로를 덧붙인다
            string folder = Path.Combine(Path.GetTempPath(), "qaplay-selftest-" + Guid.NewGuid().ToString("N"));
            string summary = Path.Combine(folder, "summary.md"), output = Path.Combine(folder, "output.txt");
            string oldSummary = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY"), oldOutput = Environment.GetEnvironmentVariable("GITHUB_OUTPUT");
            try
            {
                Directory.CreateDirectory(folder);
                Environment.SetEnvironmentVariable("GITHUB_STEP_SUMMARY", null);
                Environment.SetEnvironmentVariable("GITHUB_OUTPUT", null);
                GithubActions.Write(new List<Outcome> { passed }, "b", "d", "C:\\reports\\1");
                Check(Directory.GetFiles(folder).Length == 0, "nothing is written outside GitHub Actions");
                Environment.SetEnvironmentVariable("GITHUB_STEP_SUMMARY", summary);
                Environment.SetEnvironmentVariable("GITHUB_OUTPUT", output);
                File.WriteAllText(output, "earlier=1\n");
                GithubActions.Write(new List<Outcome> { passed }, "b", "d", "C:\\reports\\1");
                Check(File.ReadAllText(summary).StartsWith("### qaplay: 1 passed") && File.ReadAllText(output) == "earlier=1\nreport=C:\\reports\\1\n",
                    "in GitHub Actions the summary and the report folder are appended");
            }
            finally
            {
                Environment.SetEnvironmentVariable("GITHUB_STEP_SUMMARY", oldSummary);
                Environment.SetEnvironmentVariable("GITHUB_OUTPUT", oldOutput);
                Directory.Delete(folder, true);
            }
        }

        // 대시보드: 리포트를 읽어 주기만 하고, 저장소 밖의 파일과 다른 이름으로 온 요청은 거절한다
        static void DashboardServer()
        {
            string folder = Path.Combine(Path.GetTempPath(), "qaplay-selftest-" + Guid.NewGuid().ToString("N"));
            try
            {
                string root = Path.Combine(folder, "reports");
                File.WriteAllText(Directory.CreateDirectory(folder).FullName + "/victim.txt", "keep out");
                var store = new ReportStore(root);
                var hello = new Dictionary<string, object> { ["platform"] = "Android" };
                string Run(double averageMs, DateTime time)
                {
                    var sort = new Outcome
                    {
                        file = "rules/sort.yaml", rule = RuleReader.Read("name: 정렬\n---\n- tapOn: a\n"), screenshot = new byte[] { 1, 2, 3 },
                        frames = new Dictionary<string, object> { ["averageMs"] = averageMs, ["p99Ms"] = null, ["hitches"] = 2 },
                    };
                    sort.Fail("over_budget", "<b>too slow</b>");
                    return store.Record(new List<Outcome> { sort }, hello, "com.game 1.2", "Android 1080x2400", 3, null, time);
                }
                string first = Run(16.5, new DateTime(2026, 11, 1, 3, 0, 0, DateTimeKind.Utc)), second = Run(21.25, new DateTime(2026, 11, 2, 3, 0, 0, DateTimeKind.Utc));

                Dashboard.Reply Get(string target, string host = "localhost:27280", string method = "GET") => Dashboard.Handle(method, target, host, root);
                Dictionary<string, object> Data(string target) => (Dictionary<string, object>)Json.Parse(Encoding.UTF8.GetString(Get(target).body));

                Dashboard.Reply page = Get("/");
                Check(page.status == 200 && page.type.StartsWith("text/html") && Encoding.UTF8.GetString(page.body).Contains("<title>qaplay</title>"), "the page is served");
                var runs = (List<object>)Data("/api/runs")["runs"];
                Check(runs.Count == 2 && second.Equals(((Dictionary<string, object>)runs[0])["id"]), "runs are listed, newest first");

                var rule = (Dictionary<string, object>)((List<object>)Data("/api/runs/" + second)["rules"])[0];
                var shot = (Dictionary<string, object>)((List<object>)rule["evidence"])[0];
                Check("01-sort.png".Equals(shot["file"]) && false.Equals(shot["removed"]) && "<b>too slow</b>".Equals(((List<object>)rule["failures"])[0].ToString().Replace("over_budget: ", "")),
                    "a run comes with its rules and evidence");
                Dashboard.Reply png = Get("/files/" + second + "/01-sort.png");
                Check(png.status == 200 && png.type == "image/png" && png.body.Length == 3, "evidence files are served");
                File.Delete(Path.Combine(store.FolderOf(second), "01-sort.png"));
                rule = (Dictionary<string, object>)((List<object>)Data("/api/runs/" + second)["rules"])[0];
                Check(true.Equals(((Dictionary<string, object>)((List<object>)rule["evidence"])[0])["removed"]) && Get("/files/" + second + "/01-sort.png").status == 404,
                    "evidence past its retention is marked as removed");

                // 추이: 같은 기기, 같은 규칙, 같은 값끼리 오래된 것부터. 재지 못한 값(null)은 빠진다
                var series = (List<object>)Data("/api/trends")["series"];
                var average = (Dictionary<string, object>)series[0];
                var points = (List<object>)average["points"];
                Check(series.Count == 2 && "frames.averageMs".Equals(average["metric"]) && points.Count == 2
                      && 16.5.Equals(((Dictionary<string, object>)points[0])["value"]) && 21.25.Equals(((Dictionary<string, object>)points[1])["value"]),
                    "measurements become a series per rule and metric, oldest first");

                foreach (string target in new[]
                         {
                             "/files/" + first + "/..%2F..%2Fvictim.txt", "/files/" + first + "/..%5C..%5Cvictim.txt", "/files/..%2F" + "/victim.txt",
                             "/files/" + first + "/C:%5CWindows%5Cwin.ini", "/api/runs/..%2F..", "/files/retention.json", "/nope",
                         })
                    Check(Get(target).status == 404, "nothing outside a run folder is served: " + target);
                Check(Get("/api/runs", "evil.example.com").status == 403 && Get("/api/runs", "192.168.0.5:27280").status == 200 && Get("/api/runs", "[::1]:27280").status == 200
                      && Get("/api/runs", Dns.GetHostName() + ":27280").status == 200, "requests under another site's name are refused");
                Check(Get("/api/runs", "localhost", "POST").status == 405, "the dashboard only reads");

                Check(Dashboard.Endpoint("27280").Address.Equals(IPAddress.Loopback) && Dashboard.Endpoint("0.0.0.0:8080").Address.Equals(IPAddress.Any)
                      && Dashboard.Endpoint("0.0.0.0:8080").Port == 8080, "a port alone listens on this PC only");
                try { Dashboard.Endpoint("everywhere"); Check(false, "a bad --listen is refused"); }
                catch (UsageException) { }

                // 진짜 서버로 한 번: 듣고, 답하고, 멈춘다
                TcpListener listener = Dashboard.Start(root, new IPEndPoint(IPAddress.Loopback, 0));
                try
                {
                    using var http = new System.Net.Http.HttpClient();
                    System.Net.Http.HttpResponseMessage response = http.GetAsync("http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/api/runs").Result;
                    string body = response.Content.ReadAsStringAsync().Result;
                    Check((int)response.StatusCode == 200 && ((List<object>)((Dictionary<string, object>)Json.Parse(body))["runs"]).Count == 2
                          && response.Headers.Contains("X-Content-Type-Options"), "the server answers over HTTP");
                }
                finally
                {
                    listener.Stop();
                }
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }

        // 실행 리포트: 실행 하나를 폴더로 남기고, 기한에 따라 증거부터 지우고 요약은 나중에 지운다
        static void Reports()
        {
            string folder = Path.Combine(Path.GetTempPath(), "qaplay-selftest-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(folder);
                string memory = Path.Combine(folder, "leak.memory.csv"), log = Path.Combine(folder, "gift.crash.log");
                File.WriteAllText(memory, "repeat,managed\n1,10\n");
                File.WriteAllText(log, "Crash!!!\n");
                var hello = new Dictionary<string, object>
                {
                    ["platform"] = "Android", ["unity"] = "6000.3.29f1",
                    ["device"] = new Dictionary<string, object> { ["model"] = "SM-G991N", ["os"] = "Android OS 14" },
                };
                List<Outcome> Outcomes()
                {
                    var passed = new Outcome { file = "rules/leak.yaml", rule = RuleReader.Read("tc: TC-1\n---\n- tapOn: a\n"), passed = true, memoryLog = memory };
                    var failed = new Outcome { file = "rules/gift.yaml", rule = RuleReader.Read("tc: TC-2\n---\n- tapOn: a\n"), logPath = log, screenshot = new byte[] { 1, 2, 3 } };
                    failed.Fail("crash", "the game crashed");
                    return new List<Outcome> { passed, failed };
                }

                var store = new ReportStore(Path.Combine(folder, "reports"));
                var day = new DateTime(2026, 11, 1, 3, 0, 0, DateTimeKind.Utc);
                string id = store.Record(Outcomes(), hello, "com.game 1.2", "Android 1080x2400", 12.34, new RunEvidence(), day);
                string run = store.FolderOf(id);
                Check(File.Exists(Path.Combine(run, "run.json")) && File.Exists(Path.Combine(run, "01-leak.memory.csv"))
                      && File.Exists(Path.Combine(run, "02-gift.log")) && File.ReadAllBytes(Path.Combine(run, "02-gift.png")).Length == 3,
                    "a run is kept as a folder with its evidence");
                Check(store.Record(Outcomes(), hello, "com.game 1.2", "Android 1080x2400", 1, null, day) == id + "-2", "two runs in the same second get different ids");
                Directory.Delete(store.FolderOf(id + "-2"), true);

                Dictionary<string, object> saved = store.Load("latest");
                Check(id.Equals(saved["id"]) && "Android OS 14".Equals(saved["os"]) && Convert.ToInt32(saved["failed"]) == 1 && ReportStore.StatusOf(saved) == "failed"
                      && ReportStore.TimeOf(saved) == day, "the summary says what ran, where and when");
                var second = (Dictionary<string, object>)((List<object>)saved["rules"])[1];
                Check("TC-2".Equals(second["tc"]) && "failed".Equals(second["status"]) && ((List<object>)second["evidence"]).Count == 2, "each rule has its result and evidence");

                // 기본 기한: 통과한 규칙의 증거 14일, 실패한 규칙의 증거 90일, 요약 365일
                Retention retention = Retention.Load(store);
                Check(store.Clean(retention, day.AddDays(13), true).Count == 0, "nothing is removed within the retention");
                List<string> preview = store.Clean(retention, day.AddDays(15), false);
                Check(preview.Count == 1 && preview[0] == id + "/01-leak.memory.csv" && File.Exists(Path.Combine(run, "01-leak.memory.csv")), "the preview lists without removing");
                store.Clean(retention, day.AddDays(15), true);
                Check(!File.Exists(Path.Combine(run, "01-leak.memory.csv")) && File.Exists(Path.Combine(run, "02-gift.png")), "evidence of a passed rule goes first");

                // 중요 표시를 한 실행은 기한이 지나도 남는다
                saved["keep"] = true;
                store.Save(saved);
                Check(store.Clean(retention, day.AddDays(1000), true).Count == 0 && File.Exists(Path.Combine(run, "02-gift.png")), "a kept run is never cleaned up");
                saved["keep"] = false;
                store.Save(saved);

                // 사용자 규칙: 실패한 화면은 계속 둔다. 기본 기한보다 길면 그쪽을 따르고, 증거가 남아 있는 동안은 요약도 남는다
                File.WriteAllText(Path.Combine(store.Root, "retention.json"),
                    "{\"failed\": 30, \"rules\": [{\"when\": {\"result\": \"failed\", \"kind\": \"screenshot\", \"tc\": \"TC-*\"}, \"days\": \"forever\"}]}");
                retention = Retention.Load(store);
                List<string> removed = store.Clean(retention, day.AddDays(400), true);
                Check(removed.Count == 1 && removed[0] == id + "/02-gift.log" && File.Exists(Path.Combine(run, "02-gift.png")) && File.Exists(Path.Combine(run, "run.json")),
                    "a custom rule keeps what it matches longer, and the summary with it");

                // run.json이 폴더 밖의 파일을 가리켜도 따라가지 않는다
                string victim = Path.Combine(folder, "victim.txt");
                File.WriteAllText(victim, "keep me");
                ((List<object>)saved["evidence"]).Add(new Dictionary<string, object> { ["kind"] = "log", ["file"] = "../../victim.txt" });
                store.Save(saved);
                File.Delete(Path.Combine(store.Root, "retention.json"));
                removed = store.Clean(Retention.Load(store), day.AddDays(400), true);
                Check(File.Exists(victim) && removed.Contains(id + "/") && !Directory.Exists(run), "past the summary's retention the whole run goes, and nothing outside it");

                // 깨진 run.json은 건너뛴다: 그것 때문에 목록과 정리가 죽지 않고, 지워지지도 않는다
                string broken = Path.Combine(store.Root, "20200101-000000");
                Directory.CreateDirectory(broken);
                File.WriteAllText(Path.Combine(broken, "run.json"), "{\"time\": \"2020-01-01T00:00:00Z\", \"rules\": [");
                Check(store.Clean(Retention.Load(store), day.AddDays(4000), true).Count == 0 && store.Unreadable.Count == 1 && Directory.Exists(broken),
                    "a run that cannot be read is left alone");

                foreach (string bad in new[] { "{\"pased\": 3}", "{\"rules\": [{\"when\": {\"reslt\": \"failed\"}, \"days\": 1}]}", "{\"passed\": \"soon\"}" })
                {
                    File.WriteAllText(Path.Combine(store.Root, "retention.json"), bad);
                    try { Retention.Load(store); Check(false, "a mistyped retention setting is refused: " + bad); }
                    catch (UsageException) { }
                }
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }

        static void ExcelSheets()
        {
            string folder = Path.Combine(Path.GetTempPath(), "qaplay-selftest-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(folder, "rules"));
                File.WriteAllText(Path.Combine(folder, "rules", "buy.yaml"), "tc: TC-1\n---\n- tapOn: 구매\n");
                string file = Path.Combine(folder, "qa.xlsx"), settings = Path.Combine(folder, "sheet.json");
                using (var workbook = new ClosedXML.Excel.XLWorkbook())
                {
                    ClosedXML.Excel.IXLWorksheet tc = workbook.AddWorksheet("TC 목록");
                    string[] header = { "번호", "내용", "기대 결과", "우선순위", "작성일" };
                    for (int c = 0; c < header.Length; c++) tc.Cell(1, c + 1).Value = header[c];
                    tc.Row(1).Style.Font.Bold = true;
                    tc.Cell(1, 1).Style.Fill.BackgroundColor = ClosedXML.Excel.XLColor.Yellow;
                    tc.Column(2).Width = 40;
                    tc.Cell(2, 1).Value = "TC-1";
                    tc.Cell(2, 2).Value = "구매를 누른다";
                    tc.Cell(2, 3).Value = "골드가 100 준다";
                    tc.Cell(2, 4).Value = 3;                             // 숫자로 적은 칸
                    tc.Cell(2, 5).Value = new DateTime(2026, 11, 1);     // 날짜로 적은 칸
                    tc.Cell(3, 1).Value = "TC-2";
                    tc.Cell(3, 2).Value = "판매를 누른다";
                    tc.Cell(3, 3).FormulaA1 = "\"골드가 \" & 50*2 & \" 는다\"";   // 수식. 계산된 값은 파일에 들어가지 않는다
                    workbook.AddWorksheet("메모").Cell(1, 1).Value = "도구가 건드리지 않는 탭";
                    workbook.SaveAs(file);
                }
                File.WriteAllText(settings,
                    "{\"type\":\"xlsx\",\"file\":\"qa.xlsx\",\"tc\":\"TC 목록\",\"summary\":\"요약\",\"rules\":[\"rules\"]," +
                    "\"columns\":{\"tc\":\"번호\",\"content\":\"내용\",\"expected\":\"기대 결과\",\"result\":\"결과\"},\"labels\":{\"passed\":\"통과\"}}");
                var args = new Args(new[] { "run", "--sheet", settings });
                SheetConfig config = SheetConfig.Load(args);

                Check(new TcSheet(config).List().Count == 2, "test cases are read from the tab");
                Check(new TcSheet(config).List()[1].cells["기대 결과"].Equals("골드가 100 는다"),"a formula is read as its value");
                var ran = new Outcome { file = "buy.yaml", rule = RuleReader.Read("tc: TC-1\n---\n- tapOn: a\n"), passed = true };
                new TcSheet(config).Record(new List<Outcome> { ran }, "com.game 1.2", "Android");
                new TcSheet(config).Record(new List<Outcome> { ran }, "com.game 1.3", "Android");

                using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read))
                using (var workbook = new ClosedXML.Excel.XLWorkbook(stream))
                {
                    ClosedXML.Excel.IXLWorksheet tc = workbook.Worksheet("TC 목록");
                    Check(tc.Cell(1, 6).GetString() == "Rule" && tc.Cell(1, 7).GetString() == "결과", "tool columns are added after the human ones");
                    Check(tc.Cell(2, 6).GetString() == "ok" && tc.Cell(2, 7).GetString() == "통과", "rule status and latest result");
                    Check(tc.Cell(2, 8).GetString().StartsWith("com.game 1.3 / Android / "), "the latest run replaces the one before");
                    Check(tc.Cell(3, 6).GetString() == "missing", "a TC without a rule is marked");
                    Check(tc.Cell(1, 1).Style.Font.Bold && tc.Cell(1, 1).Style.Fill.BackgroundColor == ClosedXML.Excel.XLColor.Yellow
                          && Math.Abs(tc.Column(2).Width - 40) < 0.01, "the formatting people made stays");
                    Check(tc.Cell(2, 4).DataType == ClosedXML.Excel.XLDataType.Number && tc.Cell(2, 4).GetValue<int>() == 3
                          && tc.Cell(2, 5).DataType == ClosedXML.Excel.XLDataType.DateTime && tc.Cell(3, 3).HasFormula, "human cells keep their kind of value");
                    Check(workbook.Worksheet("메모").Cell(1, 1).GetString() == "도구가 건드리지 않는 탭", "other tabs stay");

                    ClosedXML.Excel.IXLWorksheet summary = workbook.Worksheet("요약");
                    Check(summary.Cell(1, 1).GetString() == "Build" && summary.Cell(3, 1).GetString() == "com.game 1.3" && summary.LastRowUsed().RowNumber() == 3,
                        "the summary tab is created and gets a row per run");
                    Check(summary.Cell(2, 4).DataType == ClosedXML.Excel.XLDataType.Number && summary.Cell(2, 8).GetString() == "100%", "counts are numbers");
                }

                // Excel이 열어 둔 것처럼 다른 쪽이 잡고 있으면: 읽기는 되고, 쓰기는 알리고 끝나며, 원본과 폴더는 그대로다
                byte[] before = File.ReadAllBytes(file);
                using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
                {
                    Check(new TcSheet(config).List().Count == 2, "a workbook open elsewhere can still be read");
                    try { new TcSheet(config).Record(null, null, null); Check(false, "writing to a workbook open elsewhere is refused"); }
                    catch (IOException) { }
                }
                Check(File.ReadAllBytes(file).AsSpan().SequenceEqual(before) && Directory.GetFiles(folder, "*.xlsx").Length == 1,
                    "a refused write leaves the workbook as it was and no temporary file");

                File.WriteAllText(settings, "{\"type\":\"xlsx\",\"tc\":\"TC 목록\"}");
                try { SheetConfig.Load(args); Check(false, "an xlsx sheet without a file is refused"); }
                catch (UsageException) { }
            }
            finally
            {
                Directory.Delete(folder, true);
            }
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
