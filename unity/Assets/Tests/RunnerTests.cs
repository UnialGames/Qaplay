using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using NUnit.Framework;
using Qaplay;
using Qaplay.Agent;
using Qaplay.Injection;
using Qaplay.UGUI;
using UnityEngine;
using UnityEngine.TestTools;

// 러너(qaplay 실행 파일)와 Python 스크립트를 실제 프로세스로 띄워, 소켓을 거쳐 샘플 화면을 다루게 한다.
// 러너를 먼저 빌드해야 한다: dotnet build runner -c Release
public class RunnerTests
{
    AgentServer m_Server;
    string m_Repo, m_Exe, m_Temp;
    int m_ExitCode;
    string m_Output;   // 표준 출력과 표준 오류를 함께
    string m_Stdout;   // 표준 출력만

    [SetUp]
    public void FindRunner()
    {
        // 저장소 위치: 환경 변수 QAPLAY_REPO, 없으면 이 프로젝트의 위 폴더
        m_Repo = Environment.GetEnvironmentVariable("QAPLAY_REPO") ?? Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));
        m_Exe = Path.Combine(m_Repo, "runner", "bin", "Release", "net10.0",
            Application.platform == RuntimePlatform.WindowsEditor ? "qaplay.exe" : "qaplay");
        if (!File.Exists(m_Exe)) Assert.Ignore("runner is not built: dotnet build runner -c Release (" + m_Exe + ")");

        m_Server = AgentServer.Start(0);
        m_Temp = Path.Combine(Application.temporaryCachePath, "qaplay-runner-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(m_Temp);
    }

    [TearDown]
    public void Cleanup()
    {
        if (m_Server != null) m_Server.Stop();
        if (m_Temp != null && Directory.Exists(m_Temp)) Directory.Delete(m_Temp, true);
    }

    string Port => m_Server.Port.ToString();

    string Write(string name, string content)
    {
        string path = Path.Combine(m_Temp, name);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    static object Gold => GameValueReader.Get(new[] { "gold" })["gold"];

    // 러너를 띄우고 끝날 때까지 게임 프레임을 계속 돌린다(러너의 요청을 메인 스레드가 처리해야 한다)
    IEnumerator RunQaplay(params string[] arguments)
    {
        var quoted = new StringBuilder();
        foreach (string argument in arguments) quoted.Append('"').Append(argument).Append("\" ");
        var start = new ProcessStartInfo(m_Exe, quoted.ToString())
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        start.WorkingDirectory = m_Temp;
        start.EnvironmentVariables["PYTHONPATH"] = Path.Combine(m_Repo, "python");
        start.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";

        var output = new StringBuilder();
        var stdout = new StringBuilder();
        using (var process = new Process { StartInfo = start })
        {
            DataReceivedEventHandler collect = (_, e) =>
            {
                if (e.Data != null) lock (output) output.AppendLine(e.Data);
            };
            process.OutputDataReceived += collect;
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null) lock (output) stdout.AppendLine(e.Data);
            };
            process.ErrorDataReceived += collect;
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            float started = Time.realtimeSinceStartup;
            while (!process.HasExited)
            {
                if (Time.realtimeSinceStartup - started > 60f)
                {
                    process.Kill();
                    Assert.Fail("runner did not finish in 60s: " + output);
                }
                yield return null;
            }
            process.WaitForExit(); // 남은 출력을 다 받는다
            m_ExitCode = process.ExitCode;
        }
        m_Output = output.ToString();
        m_Stdout = stdout.ToString();
    }

    IEnumerator FreshScreen()
    {
        SampleShop.Build();
        yield return null;
    }

    [UnityTest]
    public IEnumerator Run_SampleRule_PassesAndWritesJunit()
    {
        yield return FreshScreen();
        string junit = Path.Combine(m_Temp, "reports", "junit.xml");
        yield return RunQaplay("run", Path.Combine(Application.dataPath, "Sample", "Rules", "shop.yaml"), "--port", Port, "--junit", junit);

        Assert.AreEqual(0, m_ExitCode, m_Output);
        StringAssert.Contains("PASS  ", m_Output);
        StringAssert.Contains("1 rule(s): 1 passed, 0 flaky, 0 failed", m_Output);
        Assert.AreEqual(1100, Gold);

        string xml = File.ReadAllText(junit);
        StringAssert.Contains("tests=\"1\"", xml);
        StringAssert.Contains("failures=\"0\"", xml);
        StringAssert.Contains("classname=\"TC-012\"", xml);
        StringAssert.Contains("name=\"상점 구매\"", xml);
        StringAssert.Contains("name=\"platform\" value=\"" + Application.platform + "\"", xml);
    }

    // AI 도구가 규칙을 쓸 때 쓰는 조회 명령: 화면의 요소와 게임 값을 사람이 읽는 글로 보여 준다
    [UnityTest]
    public IEnumerator DumpAndValues_ShowWhatARuleCanUse()
    {
        yield return FreshScreen();
        yield return RunQaplay("dump", "--port", Port);
        Assert.AreEqual(0, m_ExitCode, m_Output);
        StringAssert.Contains("Canvas  Node" + Environment.NewLine, m_Output);
        StringAssert.Contains("  BuyButton  Button  id=shop.buy  \"구매\"  [tappable]" + Environment.NewLine, m_Output);
        StringAssert.Contains("    Text  Text  \"구매\"" + Environment.NewLine, m_Output);
        StringAssert.Contains("  SoldOutButton  Button  \"품절\"" + Environment.NewLine, m_Output);
        StringAssert.Contains("  FadedPanel  Image  [hidden]" + Environment.NewLine, m_Output);

        yield return RunQaplay("values", "--port", Port);
        Assert.AreEqual(0, m_ExitCode, m_Output);
        StringAssert.Contains("gift.requests = 0" + Environment.NewLine + "gold = 1200" + Environment.NewLine, m_Output);
    }

    // --json: 표준 출력에는 JSON 하나만 나온다. 사람이 읽는 줄은 표준 오류로 간다
    [UnityTest]
    public IEnumerator Json_PrintsOnlyTheResultOnStdout()
    {
        yield return FreshScreen();
        yield return RunQaplay("values", "--json", "--port", Port);
        Assert.AreEqual(1200.0, Map(StdoutJson["values"])["gold"], m_Output);
        StringAssert.Contains("gold = 1200", m_Output);

        yield return RunQaplay("dump", "--port", Port, "--json");
        Assert.AreEqual("Canvas", Map(((List<object>)StdoutJson["roots"])[0])["name"], m_Output);

        yield return RunQaplay("check", Write("check.yaml", "- tapOn: { id: shop.buy }\n- tapOn: { name: Item }\n"), "--json", "--port", Port);
        Assert.AreEqual(1, m_ExitCode, m_Output);
        Dictionary<string, object> issue = Map(((List<object>)StdoutJson["issues"])[0]);
        Assert.AreEqual("many", issue["kind"]);
        Assert.AreEqual(2.0, issue["command"]);
        Assert.AreEqual("- tapOn: { name: Item }", issue["source"]);

        // 문법이 틀린 파일은 게임에 붙기 전에 알린다
        yield return RunQaplay("check", Write("bad.yaml", "- tapp: 구매\n"), "--json", "--port", "1");
        Assert.AreEqual(1, m_ExitCode, m_Output);
        Assert.AreEqual("rule line 1: unknown command 'tapp'", StdoutJson["syntaxError"]);

        yield return RunQaplay("run", Write("fail.yaml", "name: 실패\n---\n- assertValue: { gold: 999 }\n"),
            Write("ok.yaml", "- assertValue: { gold: 1200 }\n"), "--json", "--retries", "0", "--port", Port);
        Assert.AreEqual(1, m_ExitCode, m_Output);
        Dictionary<string, object> run = StdoutJson;
        Assert.AreEqual(1.0, run["passed"]);
        Assert.AreEqual(1.0, run["failed"]);
        Dictionary<string, object> failed = Map(((List<object>)run["rules"])[0]);
        Assert.AreEqual("failed", failed["status"]);
        Assert.AreEqual("실패", failed["name"]);
        Assert.AreEqual("value_mismatch", failed["code"]);
        StringAssert.Contains("gold: expected 999, got 1200", (string)((List<object>)failed["failures"])[0]);
        Assert.AreEqual("passed", Map(((List<object>)run["rules"])[1])["status"]);

        yield return RunQaplay("devices", "--json");
        Assert.AreEqual(2, m_ExitCode, m_Output);
        StringAssert.Contains("--json is supported by run, check, dump and values", m_Output);
    }

    static Dictionary<string, object> Map(object value) => (Dictionary<string, object>)value;

    Dictionary<string, object> StdoutJson => Map(Json.Parse(m_Stdout.Trim()));

    // 러너를 거쳐도 연타와 동시 클릭이 같은 결과를 낸다: 멀쩡한 버튼의 연타는 통과하고, 버그를 심은 샘플 규칙 둘은 실패한다
    [UnityTest]
    public IEnumerator Run_StressRules_CatchTheSampleBugs()
    {
        yield return FreshScreen();
        yield return RunQaplay("run", Path.Combine(Application.dataPath, "Sample", "Rules", "rapid-buy.yaml"), "--port", Port);
        Assert.AreEqual(0, m_ExitCode, m_Output);
        Assert.AreEqual(700, Gold);

        yield return FreshScreen();
        yield return RunQaplay("run", Path.Combine(Application.dataPath, "Sample", "BuggyRules"), "--port", Port, "--retries", "0");
        Assert.AreEqual(1, m_ExitCode, m_Output);
        StringAssert.Contains("gift.requests: expected 1, got 5", m_Output);
        StringAssert.Contains("popups: expected 0, got -1", m_Output);
        StringAssert.Contains("2 rule(s): 0 passed, 0 flaky, 2 failed", m_Output);
    }

    [UnityTest]
    public IEnumerator Run_FailingRule_ExitsWithOneAndSaysWhy()
    {
        yield return FreshScreen();
        string junit = Path.Combine(m_Temp, "junit.xml");
        yield return RunQaplay("run", Write("fail.yaml", "- assertValue: { gold: 999 }\n"), "--port", Port, "--retries", "0", "--junit", junit);

        Assert.AreEqual(1, m_ExitCode, m_Output);
        StringAssert.Contains("FAIL  ", m_Output);
        StringAssert.Contains("value_mismatch: gold: expected 999, got 1200", m_Output);
        StringAssert.Contains("1 rule(s): 0 passed, 0 flaky, 1 failed", m_Output);
        StringAssert.Contains("<failure type=\"value_mismatch\"", File.ReadAllText(junit));
    }

    // 한 번 실패하고 다시 돌려 통과하면 불안정이다. 종료 코드는 성공이고, 실패했던 시도는 기록에 남는다
    [UnityTest]
    public IEnumerator Run_RuleThatPassesOnTheRerun_IsFlaky()
    {
        // 첫 시도: 구매 한 번에 1100이라 실패. 다시 돌리면 한 번 더 사서 1000이 된다
        string rule = Write("flaky.yaml", "- tapOn: { id: shop.buy }\n- assertValue: { gold: 1000 }\n");
        string junit = Path.Combine(m_Temp, "junit.xml");

        yield return FreshScreen();
        yield return RunQaplay("run", rule, "--port", Port, "--junit", junit);
        Assert.AreEqual(0, m_ExitCode, m_Output);
        StringAssert.Contains("FLAKY ", m_Output);
        StringAssert.Contains("attempt 1: failed at command 2", m_Output);
        StringAssert.Contains("1 rule(s): 0 passed, 1 flaky, 0 failed", m_Output);
        string xml = File.ReadAllText(junit);
        StringAssert.Contains("failures=\"0\"", xml);
        StringAssert.Contains("<flakyFailure type=\"value_mismatch\"", xml);

        // 다시 돌리지 않게 하면 같은 규칙이 실패다
        yield return FreshScreen();
        yield return RunQaplay("run", rule, "--port", Port, "--retries", "0");
        Assert.AreEqual(1, m_ExitCode, m_Output);
    }

    // 게임에서 C# 예외가 나면 확인 명령이 모두 통과해도 실패이고, 다시 돌리지 않는다
    [UnityTest]
    public IEnumerator Run_ExceptionInTheGame_FailsTheRuleWithoutRerun()
    {
        yield return FreshScreen();
        using (var quiet = new QuietExceptions(m_Server, SampleException))
        {
            string junit = Path.Combine(m_Temp, "junit.xml");
            yield return RunQaplay("run", Write("exception.yaml", "- tapOn: { name: ExceptionButton }\n- assertValue: { gold: 1200 }\n"),
                "--port", Port, "--junit", junit);

            Assert.AreEqual(1, m_ExitCode, m_Output);
            StringAssert.Contains("attempt 1: exception: InvalidOperationException: sample exception from ExceptionButton", m_Output);
            StringAssert.DoesNotContain("attempt 2", m_Output);
            StringAssert.Contains("<failure type=\"exception\"", File.ReadAllText(junit));

            // 탭 한 번에 클릭은 한 번이다. 버튼이 뒤늦게 한 번 더 눌리면 예외가 또 난다
            for (int frame = 0; frame < 30; frame++) yield return null;
            Assert.AreEqual(1, quiet.Count, "예외 버튼이 눌린 횟수");
        }
    }

    // 샘플의 예외 버튼이 내는 예외. 테스트가 일부러 내는 것이라 Console에 남기지 않는다(QuietExceptions)
    const string SampleException = "sample exception from ExceptionButton";

    // 폴더를 주면 그 안의 규칙을 모두 돌린다. 문법이 틀린 파일은 실행하지 않고 실패로 센다
    [UnityTest]
    public IEnumerator Run_Folder_RunsEveryRuleAndReportsSyntaxErrors()
    {
        yield return FreshScreen();
        Write("a_ok.yaml", "- assertValue: { gold: 1200 }\n");
        Write("b_bad.yaml", "- tapp: 구매\n");
        yield return RunQaplay("run", m_Temp, "--port", Port);

        Assert.AreEqual(1, m_ExitCode, m_Output);
        StringAssert.Contains("rule line 1: unknown command 'tapp'", m_Output);
        StringAssert.Contains("2 rule(s): 1 passed, 0 flaky, 1 failed", m_Output);
    }

    [UnityTest]
    public IEnumerator Check_ReportsIssuesWithoutTapping()
    {
        yield return FreshScreen();
        yield return RunQaplay("check", Write("check.yaml", "- tapOn: { id: shop.buy }\n- tapOn: { name: Item }\n- tapOn: { name: Nope }\n"), "--port", Port);

        Assert.AreEqual(1, m_ExitCode, m_Output);
        StringAssert.Contains("command 2 (line 2): - tapOn: { name: Item } -> many: selector matched 6 elements", m_Output);
        StringAssert.Contains("command 3 (line 3): - tapOn: { name: Nope } -> none", m_Output);
        StringAssert.Contains("2 issue(s) on the current screen", m_Output);
        Assert.AreEqual(1200, Gold);
    }

    [UnityTest]
    public IEnumerator Run_WhenTheGameCannotBeReached_ExitsWithTwo()
    {
        // 아무도 듣지 않는 포트
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int closedPort = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        yield return RunQaplay("run", Path.Combine(Application.dataPath, "Sample", "Rules", "shop.yaml"), "--port", closedPort.ToString());
        Assert.AreEqual(2, m_ExitCode, m_Output);
        StringAssert.Contains("cannot reach the game", m_Output);
    }

    // Python 스크립트 → 러너의 중계 → 에이전트. 스크립트의 종료 코드가 러너의 종료 코드가 된다
    [UnityTest]
    public IEnumerator Serve_RunsAPythonScriptThroughTheRelay()
    {
        yield return FreshScreen();
        yield return RunQaplay("serve", "--listen", "0", "--port", Port, "--", "python", Path.Combine(m_Repo, "python", "examples", "shop.py"));
        if (m_ExitCode == 2 && m_Output.Contains("cannot run python")) Assert.Ignore("python is not installed");

        Assert.AreEqual(0, m_ExitCode, m_Output);
        StringAssert.Contains("ok: gold 1200 -> 800 on " + Application.platform, m_Output);
        Assert.AreEqual(800, Gold);

        // 실패한 명령은 오류 코드가 담긴 예외가 되고, 잡지 않으면 스크립트가 실패로 끝난다
        yield return FreshScreen();
        string script = Write("fail.py", "import qaplay\nwith qaplay.connect() as game:\n    game.tap(name='Nope', timeout_ms=0)\n");
        yield return RunQaplay("serve", "--listen", "0", "--port", Port, "--", "python", script);
        Assert.AreEqual(1, m_ExitCode, m_Output);
        StringAssert.Contains("qaplay.QaplayError: not_found", m_Output);

        // 연타와 동시 클릭도 Python에서 같은 결과를 낸다
        yield return FreshScreen();
        script = Write("stress.py",
            "import qaplay\n" +
            "with qaplay.connect() as game:\n" +
            "    game.tap(id='shop.buy', repeat=5)\n" +
            "    game.tap(name='SellButton')\n" +
            "    points = game.tap_together({'name': 'ConfirmButton'}, '취소')['points']\n" +
            "    game.wait_gone(name='SellPopup')\n" +
            "    print('stress:', game.get('gold'), game.get('popups'), len(points))\n");
        yield return RunQaplay("serve", "--listen", "0", "--port", Port, "--", "python", script);
        Assert.AreEqual(0, m_ExitCode, m_Output);
        StringAssert.Contains("stress: 1000 -1 2", m_Output);
    }

    static void ClearDeviceReports()
    {
        if (Directory.Exists(BugReporter.Folder)) Directory.Delete(BugReporter.Folder, true);
    }

    // 끝났다는 기준: 제보하면 조작 이력이 붙은 리포트가 생기고, 명령 하나로 회귀 규칙이 된다
    [UnityTest]
    public IEnumerator Bugs_AReportBecomesARegressionRule()
    {
        ClearDeviceReports();
        yield return FreshScreen();
        UguiRecorder.ForgetHistory();

        // 테스터가 구매를 누르고, 이상하다고 보고 제보한다(F12를 누른 것과 같다)
        Vector2 buy = UguiTap.PointOf(UguiDumper.Dump(), new UiSelector { id = "shop.buy" });
        yield return TouchInjector.Tap(buy.x, buy.y);
        yield return BugReporter.Create(null, null, _ => { });

        // PC로 가져온다. 가져간 제보는 기기에서 지워진다
        yield return RunQaplay("bugs", "pull", "--port", Port);
        Assert.AreEqual(0, m_ExitCode, m_Output);
        StringAssert.Contains("0001  Report 20", m_Output);
        Assert.AreEqual(0, BugReporter.List().Count);
        string bugFolder = Path.Combine(m_Temp, "qaplay-bugs", "0001");
        FileAssert.Exists(Path.Combine(bugFolder, "bug.json"));
        FileAssert.Exists(Path.Combine(bugFolder, "log.txt"));
        StringAssert.Contains("- tapOn: { id: shop.buy }", File.ReadAllText(Path.Combine(bugFolder, "steps.yaml")));

        yield return RunQaplay("bugs", "pull", "--port", Port);
        StringAssert.Contains("no new reports", m_Output);

        // 제목과 심각도는 PC에서 적는다
        yield return RunQaplay("bugs", "set", "1", "--title", "구매하면 골드 표시가 늦게 바뀜", "--severity", "minor");
        Assert.AreEqual(0, m_ExitCode, m_Output);
        yield return RunQaplay("bugs", "list", "--status", "open");
        StringAssert.Contains("0001  open", m_Output);
        StringAssert.Contains("x1  구매하면 골드 표시가 늦게 바뀜", m_Output);
        yield return RunQaplay("bugs", "set", "1", "--severity", "terrible");
        Assert.AreEqual(2, m_ExitCode, m_Output);

        // 명령 하나로 회귀 규칙이 된다
        string regression = Path.Combine(m_Temp, "regression");
        yield return RunQaplay("bugs", "promote", "1", "--to", regression);
        Assert.AreEqual(0, m_ExitCode, m_Output);
        string rulePath = Path.Combine(regression, "bug-0001.yaml");
        Rule rule = RuleReader.Read(File.ReadAllText(rulePath));
        Assert.AreEqual("regression: 구매하면 골드 표시가 늦게 바뀜", rule.name);
        Assert.AreEqual(1, rule.steps.Count);
        Assert.AreEqual("tap", rule.steps[0].cmd);

        // 사람이 통과 조건을 덧붙이고, 고쳤다고 표시한 뒤 확인 명령을 돌린다
        File.AppendAllText(rulePath, "- assertValue: { gold: 1100 }\n");
        yield return RunQaplay("bugs", "set", "1", "--status", "resolved");
        yield return FreshScreen();
        yield return RunQaplay("bugs", "verify", "1", "--port", Port);
        Assert.AreEqual(0, m_ExitCode, m_Output);
        StringAssert.Contains("bug 0001 verified", m_Output);
        yield return RunQaplay("bugs", "show", "1");
        StringAssert.Contains("status: verified", m_Output);

        // 확인까지 된 버그의 규칙이 다시 실패하면 회귀로 다시 열린다
        File.AppendAllText(rulePath, "- assertValue: { gold: 1 }\n");
        yield return FreshScreen();
        yield return RunQaplay("bugs", "verify", "1", "--port", Port, "--retries", "0");
        Assert.AreEqual(1, m_ExitCode, m_Output);
        yield return RunQaplay("bugs", "show", "1");
        StringAssert.Contains("status: open", m_Output);
        StringAssert.Contains("regression: true", m_Output);

        yield return RunQaplay("bugs", "delete", "1");
        Assert.IsFalse(Directory.Exists(bugFolder));
    }

    // 러너가 잡은 예외는 버그로 바로 올라가고, 같은 예외는 한 버그로 묶인다
    [UnityTest]
    public IEnumerator Run_RegistersExceptionsAsBugsAndGroupsRepeats()
    {
        ClearDeviceReports();
        string rule = Write("exception.yaml", "- tapOn: { name: ExceptionButton }\n");
        using (var quiet = new QuietExceptions(m_Server, SampleException))
        {
            for (int run = 1; run <= 2; run++)
            {
                yield return FreshScreen();
                yield return RunQaplay("run", rule, "--port", Port);
                Assert.AreEqual(1, m_ExitCode, m_Output);
                StringAssert.Contains(run == 1 ? "bug 0001 registered" : "bug 0001 seen 2 times", m_Output);
            }

            yield return RunQaplay("bugs", "show", "1");
            StringAssert.Contains("title: InvalidOperationException: sample exception from ExceptionButton", m_Output);
            StringAssert.Contains("kind: exception", m_Output);
            StringAssert.Contains("severity: major", m_Output);
            StringAssert.Contains("count: 2", m_Output);
            StringAssert.Contains("- tapOn: { name: ExceptionButton }", m_Output);

            // 올리지 않게 할 수도 있다
            yield return FreshScreen();
            yield return RunQaplay("run", rule, "--port", Port, "--bugs", "off");
            StringAssert.DoesNotContain("bug 0001", m_Output);
            Assert.AreEqual(3, quiet.Count, "예외 버튼이 눌린 횟수");
        }

        // 지금 화면을 PC에서 바로 제보한다
        yield return RunQaplay("bugs", "report", "--title", "PC에서 건 제보", "--port", Port);
        Assert.AreEqual(0, m_ExitCode, m_Output);
        StringAssert.Contains("0002  PC에서 건 제보", m_Output);
    }
}
