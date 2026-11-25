using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using NUnit.Framework;
using Qaplay;
using Qaplay.Agent;
using UnityEngine;
using UnityEngine.TestTools;

// 러너(qaplay 실행 파일)와 Python 스크립트를 실제 프로세스로 띄워, 소켓을 거쳐 샘플 화면을 다루게 한다.
// 러너를 먼저 빌드해야 한다: dotnet build runner -c Release
public class RunnerTests
{
    AgentServer m_Server;
    string m_Repo, m_Exe, m_Temp;
    int m_ExitCode;
    string m_Output;

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
        start.EnvironmentVariables["PYTHONPATH"] = Path.Combine(m_Repo, "python");
        start.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";

        var output = new StringBuilder();
        using (var process = new Process { StartInfo = start })
        {
            DataReceivedEventHandler collect = (_, e) =>
            {
                if (e.Data != null) lock (output) output.AppendLine(e.Data);
            };
            process.OutputDataReceived += collect;
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
        LogAssert.Expect(LogType.Exception, new System.Text.RegularExpressions.Regex("sample exception from ExceptionButton"));
        string junit = Path.Combine(m_Temp, "junit.xml");
        yield return RunQaplay("run", Write("exception.yaml", "- tapOn: { name: ExceptionButton }\n- assertValue: { gold: 1200 }\n"),
            "--port", Port, "--junit", junit);

        Assert.AreEqual(1, m_ExitCode, m_Output);
        StringAssert.Contains("attempt 1: exception: InvalidOperationException: sample exception from ExceptionButton", m_Output);
        StringAssert.DoesNotContain("attempt 2", m_Output);
        StringAssert.Contains("<failure type=\"exception\"", File.ReadAllText(junit));
    }

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
    }
}
