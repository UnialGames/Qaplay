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
        StringAssert.Contains("--json is supported by run, check, dump, values, tc, reports, layout, exceptions, mocks, memory, frames and device", m_Output);
    }

    // 지금 쓰는 메모리를 보고(qaplay memory), 그 숫자로 예산을 적은 규칙을 돌린다
    [UnityTest]
    public IEnumerator Memory_ShowsTheUsageAndABudgetRuleFailsOverIt()
    {
        yield return FreshScreen();
        yield return RunQaplay("memory", "--json", "--port", Port);
        Assert.AreEqual(0, m_ExitCode, m_Output);
        // 사람이 읽는 줄(표준 오류)과 JSON(표준 출력)은 섞여 도착할 수 있어 줄마다 본다
        foreach (string layer in new[] { "managed", "native", "process" })
            StringAssert.IsMatch(layer + @" +\d+\.\d MB", m_Output);
        Assert.Greater((double)StdoutJson["managed"], 1024.0 * 1024.0, "JSON은 바이트 그대로다");

        yield return RunQaplay("run", Write("budget.yaml", "- assertMemory: { managed: 0.5 }\n"), "--retries", "0", "--port", Port);
        Assert.AreEqual(1, m_ExitCode, m_Output);
        StringAssert.IsMatch(@"-> over_budget: managed: \d+\.\d MB, budget 0\.5 MB", m_Output);
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
        // 누수를 심은 보상 팝업도 이 폴더에 있다: 되풀이마다 잰 메모리가 파일로 남는다
        StringAssert.IsMatch(@"-> leak: native grew 1(\.\d+)? MB per repeat \(", m_Output);
        // 끊김을 심은 정렬 버튼도 있다: 실패한 줄 아래에 잰 프레임 시간이 나온다
        StringAssert.IsMatch(@"-> over_budget: hitches: \d+ frame\(s\) over 50 ms \(longest \d{3,}\.\d ms\), budget 0", m_Output);
        StringAssert.IsMatch(@"frames: average [\d.]+ ms, p99 [\d.]+ ms, longest [\d.]+ ms, \d+ hitch\(es\) over 50 ms, \d+ frames in [\d.]+ s", m_Output);
        // 콜백 중복을 심은 결제도 있다
        StringAssert.Contains("gems: expected 100, got 200", m_Output);
        // 글자 넘침을 심은 이벤트 패널도 있다
        StringAssert.Contains("-> layout: 2 issue(s): text is cut off in Canvas/EventPanel/Banner", m_Output);
        // 겹침을 심은 NEW 배지도 있다
        // 화면이 좁으면 앞 규칙이 열어 둔 이벤트 패널의 겹침도 함께 나오므로 건수는 보지 않는다
        StringAssert.Contains("Canvas/BuyButton overlaps Canvas/NewBadge (", m_Output);
        // 잔상을 심은 아이템 그림도 있다
        StringAssert.Contains("-> ghosting: Canvas/ItemIcon kept showing", m_Output);
        StringAssert.Contains("8 rule(s): 0 passed, 0 flaky, 8 failed", m_Output);
        string[] history = File.ReadAllLines(Path.Combine(m_Temp, "qaplay-artifacts", "leak-reward.memory.csv"));
        Assert.AreEqual("repeat,managed,native,process", history[0]);
        Assert.AreEqual(21, history.Length, "머리말과 되풀이 20번");
        StringAssert.IsMatch(@"^20,\d+,\d+,\d+$", history[20]);
    }

    // 지금 화면의 프레임 시간을 잰다(qaplay frames): 예산을 정하기 전에 본다
    [UnityTest]
    public IEnumerator Frames_MeasuresTheCurrentScreenForAWhile()
    {
        yield return FreshScreen();
        yield return RunQaplay("frames", "--seconds", "1", "--json", "--port", Port);
        Assert.AreEqual(0, m_ExitCode, m_Output);
        StringAssert.IsMatch(@"average [\d.]+ ms, p99 [\d.]+ ms, longest [\d.]+ ms, \d+ hitch\(es\) over 50 ms, \d+ frames in [\d.]+ s", m_Output);
        Assert.Greater((double)StdoutJson["count"], 5.0);
        Assert.That((double)StdoutJson["seconds"], Is.InRange(0.8, 1.5));
    }

    // 지금 화면의 배치 문제를 본다(qaplay layout): 문제가 있으면 종료 코드 1
    [UnityTest]
    public IEnumerator Layout_ListsTheOverflowsOnTheCurrentScreen()
    {
        yield return FreshScreen();
        yield return RunQaplay("layout", "--port", Port);
        Assert.AreEqual(0, m_ExitCode, m_Output);
        StringAssert.Contains("no layout issues on the current screen", m_Output);

        // 넘침을 심은 이벤트 패널을 연 뒤
        yield return RunQaplay("run", Write("open.yaml", "- tapOn: { name: EventButton }\n- assertVisible: { name: EventPanel }\n"), "--port", Port);
        yield return RunQaplay("layout", "--json", "--port", Port);
        Assert.AreEqual(1, m_ExitCode, m_Output);
        StringAssert.Contains("overflow: text is cut off in Canvas/EventPanel/Banner \"LIMITED TIME SUPER SALE - EVERYTHING MUST GO TODAY\"", m_Output);
        StringAssert.Contains("overflow: text spills out of Canvas/EventPanel/Notice", m_Output);
        StringAssert.Contains("2 layout issue(s) on the current screen", m_Output);
        var issues = (List<object>)StdoutJson["issues"];
        Assert.AreEqual(2, issues.Count);
        Assert.AreEqual("truncated", Map(issues[0])["reason"]);
        Assert.AreEqual("Banner", Map(Map(issues[0])["element"])["name"]);
    }

    // 시트의 TC를 읽고, 규칙을 실행한 결과를 TC 표의 도구 칸과 요약 표에 적는다. 저장소의 샘플 시트(samples/sheet)를 쓴다
    [UnityTest]
    public IEnumerator Sheet_ListsTestCasesAndRunWritesTheResultsBack()
    {
        yield return FreshScreen();
        string sample = Path.Combine(m_Repo, "samples", "sheet");
        if (!Directory.Exists(sample)) Assert.Ignore("the sample sheet is not here: " + sample);
        // 실행한 폴더의 qaplay-sheet.json은 --sheet 없이도 쓰인다. 규칙 폴더만 이 프로젝트의 것으로 바꾼다
        File.Copy(Path.Combine(sample, "tc.csv"), Path.Combine(m_Temp, "tc.csv"));
        Write("qaplay-sheet.json", File.ReadAllText(Path.Combine(sample, "qaplay-sheet.json"))
            .Replace("../../unity/Assets/Sample", Application.dataPath + "/Sample"));

        // 규칙을 쓰는 쪽(AI)이 TC를 읽는다
        yield return RunQaplay("tc", "list", "--json");
        Assert.AreEqual(0, m_ExitCode, m_Output);
        StringAssert.Contains("TC-012  rule: ok  result: 미실행  구매 버튼을 누른다", m_Output);
        StringAssert.Contains("TC-030  rule: missing", m_Output);
        Assert.AreEqual(6, ((List<object>)StdoutJson["cases"]).Count);

        yield return RunQaplay("tc", "show", "TC-014", "--json");
        StringAssert.Contains("기대 결과: 결제됐을 때만 보석이 100개 들어온다", m_Output);
        Assert.AreEqual("결제됐을 때만 보석이 100개 들어온다", Map(StdoutJson["cells"])["기대 결과"]);
        StringAssert.EndsWith("pay.yaml", (string)((List<object>)StdoutJson["rules"])[0]);

        // 통과하는 규칙 하나(TC-012)와, 샘플에 심은 버그를 잡는 규칙 하나(TC-020)
        yield return RunQaplay("run", Path.Combine(Application.dataPath, "Sample", "Rules", "shop.yaml"),
            Path.Combine(Application.dataPath, "Sample", "BuggyRules", "rapid-gift.yaml"), "--retries", "0", "--port", Port);
        Assert.AreEqual(1, m_ExitCode, m_Output);
        StringAssert.Contains("sheet: 2 TC(s) updated in", m_Output);
        StringAssert.Contains("sheet: a summary row added to", m_Output);

        var rows = new Dictionary<string, string[]>();
        string[] lines = File.ReadAllLines(Path.Combine(m_Temp, "tc.csv"));
        string[] header = lines[0].Split(',');
        foreach (string line in lines) rows[line.Split(',')[0]] = line.Split(',');
        string Cell(string tc, string column) => rows[tc][Array.IndexOf(header, column)];
        Assert.AreEqual("통과", Cell("TC-012", "최신 결과"));
        Assert.AreEqual("있음", Cell("TC-012", "규칙 상태"));
        StringAssert.Contains(Application.platform + " ", Cell("TC-012", "최신 실행"));
        Assert.AreEqual("실패", Cell("TC-020", "최신 결과"));
        Assert.AreEqual("", Cell("TC-013", "최신 결과"), "이번에 돌리지 않은 TC의 결과는 그대로다");
        Assert.AreEqual("있음", Cell("TC-013", "규칙 상태"));
        Assert.AreEqual("없음", Cell("TC-030", "규칙 상태"));
        Assert.AreEqual("골드 1200", Cell("TC-012", "사전 조건"), "사람이 적은 칸은 그대로다");
        // 리포트 칸에는 그 실행의 리포트 ID가 들어간다(qaplay reports show로 본다)
        StringAssert.IsMatch(@"^\d{8}-\d{6}", Cell("TC-012", "리포트"));
        Assert.IsTrue(File.Exists(Path.Combine(m_Temp, "qaplay-reports", Cell("TC-012", "리포트"), "run.json")));
        Assert.AreEqual("", Cell("TC-013", "리포트"));

        string[] summary = File.ReadAllLines(Path.Combine(m_Temp, "summary.csv"));
        Assert.AreEqual(2, summary.Length);
        StringAssert.StartsWith("빌드,실행 일시,기기,전체,통과,실패,불안정,통과율", summary[0].TrimStart('﻿'));
        StringAssert.EndsWith(",2,1,1,0,50%", summary[1]);

        // 시트 설정이 틀리면 실행하기 전에 알린다
        Write("qaplay-sheet.json", "{ \"tc\": \"nope.csv\", \"type\": \"numbers\" }");
        yield return RunQaplay("run", Path.Combine(Application.dataPath, "Sample", "Rules", "shop.yaml"), "--port", Port);
        Assert.AreEqual(2, m_ExitCode, m_Output);
        StringAssert.Contains("type must be csv", m_Output);
    }

    // 실행할 때마다 리포트가 폴더로 남는다: 조회, 중요 표시, 기한이 지난 것의 미리 보기와 정리
    [UnityTest]
    public IEnumerator Reports_EveryRunIsKeptAndCleanedUpByRetention()
    {
        yield return FreshScreen();
        string pass = Write("pass.yaml", "tc: TC-1\n---\n- assertVisible: { id: shop.buy }\n");
        string fail = Write("fail.yaml", "tc: TC-2\n---\n- assertVisible: { name: NoSuchThing, timeout: 300 }\n");
        yield return RunQaplay("run", pass, fail, "--retries", "0", "--json", "--port", Port);
        Assert.AreEqual(1, m_ExitCode, m_Output);
        string folder = (string)StdoutJson["report"];
        StringAssert.Contains("report: " + folder, m_Output);
        Assert.IsTrue(File.Exists(Path.Combine(folder, "run.json")), folder);
        string id = Path.GetFileName(folder);

        yield return RunQaplay("reports", "list", "--json");
        StringAssert.Contains("2 rule(s): 1 passed, 0 flaky, 1 failed", m_Output);
        StringAssert.Contains("1 run(s) in", m_Output);
        Assert.AreEqual(id, Map(((List<object>)StdoutJson["runs"])[0])["id"]);

        yield return RunQaplay("reports", "show", "latest", "--json");
        Assert.AreEqual(0, m_ExitCode, m_Output);
        StringAssert.Contains("fail.yaml  TC-2", m_Output);
        StringAssert.Contains("attempt 1: failed at command 1", m_Output);
        Assert.AreEqual(SystemInfo.operatingSystem, StdoutJson["os"]);
        Assert.AreEqual(Application.platform.ToString(), StdoutJson["platform"]);
        var failed = Map(((List<object>)StdoutJson["rules"])[1]);
        Assert.AreEqual("failed", failed["status"]);
        // 실패한 화면이 증거로 남는다. 배치 모드의 게임은 화면을 그리지 않아 찍을 것이 없다
        var evidence = (List<object>)failed["evidence"];
        if (Application.isBatchMode) Assert.AreEqual(0, evidence.Count);
        else
        {
            Assert.AreEqual("screenshot", Map(evidence[0])["kind"]);
            Assert.IsTrue(File.Exists(Path.Combine(folder, "02-fail.png")));
        }

        // 기한을 0일로 줄이면 바로 정리 대상이다. 미리 보기는 지우지 않는다
        yield return RunQaplay("reports", "retention", "summary=0", "passed=0", "failed=0");
        StringAssert.Contains("summary: 0 day(s)", m_Output);
        yield return RunQaplay("reports", "expired");
        StringAssert.Contains(id + "/", m_Output);
        StringAssert.Contains("1 run(s) past their retention", m_Output);
        Assert.IsTrue(Directory.Exists(folder));

        // 중요 표시를 한 실행은 정리해도 남는다
        yield return RunQaplay("reports", "keep", id);
        yield return RunQaplay("reports", "clean");
        StringAssert.Contains("0 evidence file(s) and 0 run(s) removed", m_Output);
        yield return RunQaplay("reports", "list");
        StringAssert.Contains("[kept]", m_Output);

        yield return RunQaplay("reports", "unkeep", id);
        yield return RunQaplay("reports", "clean");
        StringAssert.Contains("1 run(s) removed", m_Output);
        Assert.IsFalse(Directory.Exists(folder));

        // 오타 난 보관 설정은 실행하기 전에 알린다
        yield return RunQaplay("reports", "retention", "pased=3");
        Assert.AreEqual(2, m_ExitCode, m_Output);
        File.WriteAllText(Path.Combine(m_Temp, "qaplay-reports", "retention.json"), "{ \"pased\": 3 }");
        yield return RunQaplay("run", pass, "--port", Port);
        Assert.AreEqual(2, m_ExitCode, m_Output);
        StringAssert.Contains("unknown name \"pased\"", m_Output);

        // --reports off: 남기지 않는다
        File.Delete(Path.Combine(m_Temp, "qaplay-reports", "retention.json"));
        yield return RunQaplay("run", pass, "--reports", "off", "--port", Port);
        Assert.AreEqual(0, m_ExitCode, m_Output);
        StringAssert.DoesNotContain("report:", m_Output);
        Assert.AreEqual(0, Directory.GetDirectories(Path.Combine(m_Temp, "qaplay-reports")).Length);
    }

    // Excel 시트(xlsx)도 같은 일을 한다. 저장소의 샘플(samples/sheet-xlsx)을 쓰고, 적힌 것은 러너로 다시 읽어 확인한다
    // (탭 안의 서식과 요약 탭의 내용은 러너의 selftest가 본다)
    [UnityTest]
    public IEnumerator Sheet_ExcelWorkbookGetsTheSameResults()
    {
        yield return FreshScreen();
        string sample = Path.Combine(m_Repo, "samples", "sheet-xlsx");
        if (!Directory.Exists(sample)) Assert.Ignore("the sample workbook is not here: " + sample);
        File.Copy(Path.Combine(sample, "qa.xlsx"), Path.Combine(m_Temp, "qa.xlsx"));
        Write("qaplay-sheet.json", File.ReadAllText(Path.Combine(sample, "qaplay-sheet.json"))
            .Replace("../../unity/Assets/Sample", Application.dataPath + "/Sample"));

        yield return RunQaplay("tc", "list", "--json");
        Assert.AreEqual(0, m_ExitCode, m_Output);
        StringAssert.Contains("TC-012  rule: ok  result: 미실행  구매 버튼을 누른다", m_Output);
        StringAssert.Contains("qa.xlsx [TC]", m_Output);
        Assert.AreEqual(6, ((List<object>)StdoutJson["cases"]).Count);

        yield return RunQaplay("run", Path.Combine(Application.dataPath, "Sample", "Rules", "shop.yaml"),
            Path.Combine(Application.dataPath, "Sample", "BuggyRules", "rapid-gift.yaml"), "--retries", "0", "--port", Port);
        Assert.AreEqual(1, m_ExitCode, m_Output);
        StringAssert.Contains("sheet: 2 TC(s) updated in", m_Output);
        StringAssert.Contains("qa.xlsx [요약]", m_Output);

        yield return RunQaplay("tc", "list");
        StringAssert.Contains("TC-012  rule: ok  result: 통과", m_Output);
        StringAssert.Contains("TC-020  rule: ok  result: 실패", m_Output);
        StringAssert.Contains("TC-013  rule: ok  result: 미실행", m_Output);

        yield return RunQaplay("tc", "show", "TC-012", "--json");
        var cells = Map(StdoutJson["cells"]);
        Assert.AreEqual("있음", cells["규칙 상태"]);
        StringAssert.Contains(Application.platform + " ", (string)cells["최신 실행"]);
        Assert.AreEqual("골드 1200", cells["사전 조건"], "사람이 적은 칸은 그대로다");
    }

    // 의도된 배치 문제를 예외로 등록하면 그 뒤의 검사에서 빠진다: 등록, 조회, 사유 고치기, 지우기
    [UnityTest]
    public IEnumerator Exceptions_RegisteredIssuesAreSkippedByLayoutAndByRules()
    {
        yield return FreshScreen();
        string rule = Write("event.yaml", "- tapOn: { name: EventButton }\n- assertVisible: { name: EventPanel }\n- assertLayout: overflow\n");
        yield return RunQaplay("run", rule, "--retries", "0", "--port", Port);
        // 패널은 이미 열려 있다. 뒤에서는 열지 않고 검사만 하는 규칙을 쓴다(다시 누르면 패널이 하나 더 생긴다)
        string check = Write("check.yaml", "- assertLayout: overflow\n");
        Assert.AreEqual(1, m_ExitCode, m_Output);
        StringAssert.Contains("-> layout: 2 issue(s): text is cut off in Canvas/EventPanel/Banner", m_Output);

        // 지금 화면(이벤트 패널이 열려 있다)의 문제에 번호가 붙어 나온다
        yield return RunQaplay("layout", "--port", Port);
        StringAssert.Contains("1. overflow: text is cut off in Canvas/EventPanel/Banner", m_Output);
        StringAssert.Contains("2. overflow: text spills out of Canvas/EventPanel/Notice", m_Output);

        // 사유 없이는 등록할 수 없다
        yield return RunQaplay("exceptions", "add", "1", "--port", Port);
        Assert.AreEqual(2, m_ExitCode, m_Output);
        StringAssert.Contains("say why with --reason", m_Output);

        yield return RunQaplay("exceptions", "add", "1", "--reason", "배너는 말줄임표로 자르기로 했다", "--port", Port);
        Assert.AreEqual(0, m_ExitCode, m_Output);
        StringAssert.Contains("added: overflow: Canvas/EventPanel/Banner  -- 배너는 말줄임표로 자르기로 했다", m_Output);

        // 등록한 것은 빠지고, 남은 것이 다시 1번이 된다
        yield return RunQaplay("layout", "--json", "--port", Port);
        Assert.AreEqual(1, m_ExitCode, m_Output);
        StringAssert.Contains("1. overflow: text spills out of Canvas/EventPanel/Notice", m_Output);
        StringAssert.Contains("1 more ignored by the exceptions in qaplay-layout-exceptions.json", m_Output);
        Assert.AreEqual(1.0, StdoutJson["ignored"]);

        // 규칙도 남은 하나로만 실패한다
        yield return RunQaplay("run", check, "--retries", "0", "--port", Port);
        Assert.AreEqual(1, m_ExitCode, m_Output);
        StringAssert.Contains("-> layout: 1 issue(s): text spills out of Canvas/EventPanel/Notice", m_Output);

        // 남은 것도 등록하면 규칙이 통과하고, 넘어간 수를 알린다
        yield return RunQaplay("exceptions", "add", "1", "--reason", "임시", "--port", Port);
        yield return RunQaplay("run", check, "--retries", "0", "--json", "--port", Port);
        Assert.AreEqual(0, m_ExitCode, m_Output);
        StringAssert.Contains("layout: 2 issue(s) ignored by the exceptions", m_Output);
        Assert.AreEqual(2.0, Map(((List<object>)StdoutJson["rules"])[0])["ignored"]);

        // 조회, 사유 고치기
        yield return RunQaplay("exceptions", "set", "2", "--reason", "공지는 다음 판에서 고친다");
        Assert.AreEqual(0, m_ExitCode, m_Output);
        yield return RunQaplay("exceptions", "list", "--json");
        StringAssert.Contains("1. overflow: Canvas/EventPanel/Banner  -- 배너는 말줄임표로 자르기로 했다", m_Output);
        StringAssert.Contains("2. overflow: Canvas/EventPanel/Notice  -- 공지는 다음 판에서 고친다", m_Output);
        Assert.AreEqual(2, ((List<object>)StdoutJson["exceptions"]).Count);
        // 파일은 저장소에 두고 줄 단위로 검토한다: 한 줄에 예외 하나
        string[] lines = File.ReadAllLines(Path.Combine(m_Temp, "qaplay-layout-exceptions.json"));
        Assert.AreEqual(4, lines.Length, string.Join("\n", lines));
        StringAssert.Contains("\"path\":\"Canvas/EventPanel/Banner\"", lines[1]);

        // 지우면 다시 걸린다
        yield return RunQaplay("exceptions", "delete", "1");
        StringAssert.Contains("deleted: overflow: Canvas/EventPanel/Banner", m_Output);
        yield return RunQaplay("run", check, "--retries", "0", "--port", Port);
        Assert.AreEqual(1, m_ExitCode, m_Output);
        StringAssert.Contains("-> layout: 1 issue(s): text is cut off in Canvas/EventPanel/Banner", m_Output);

        yield return RunQaplay("exceptions", "delete", "5");
        Assert.AreEqual(2, m_ExitCode, m_Output);
        StringAssert.Contains("there is no exception 5", m_Output);
    }

    // 문제가 실제로 어떻게 보이는지 확인할 화면을 함께 받는다. 화면을 그리지 않는 배치 모드에서는 찍을 수 없다
    [UnityTest]
    public IEnumerator Layout_SavesTheScreenNextToTheIssues()
    {
        yield return FreshScreen();
        string shot = Path.Combine(m_Temp, "shots", "screen.png");
        yield return RunQaplay("layout", "--json", "--screenshot", shot, "--port", Port);
        if (Application.isBatchMode)
        {
            Assert.AreEqual(2, m_ExitCode, m_Output);
            StringAssert.Contains("the game could not capture its screen", m_Output);
            yield break;
        }
        Assert.AreEqual(0, m_ExitCode, m_Output);
        byte[] png = File.ReadAllBytes(shot);
        Assert.AreEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, new[] { png[0], png[1], png[2], png[3] }, "PNG 파일이다");
        Assert.AreEqual(Path.GetFullPath(shot), StdoutJson["screenshot"]);
        Assert.AreEqual((double)Screen.width, Map(StdoutJson["screen"])["width"]);
    }

    // 게임이 등록한 Mock을 보고(qaplay mocks), 응답을 바꿔 가는 샘플 규칙을 러너로 돌린다
    [UnityTest]
    public IEnumerator Mocks_ListsTheScenariosAndARuleLeavesThemAtTheDefault()
    {
        yield return FreshScreen();
        yield return RunQaplay("mocks", "--json", "--port", Port);
        Assert.AreEqual(0, m_ExitCode, m_Output);
        StringAssert.Contains("store = success  (success, fail, cancel, slow, duplicate)", m_Output);
        Assert.AreEqual("success", Map(Map(StdoutJson["mocks"])["store"])["scenario"]);

        yield return RunQaplay("run", Path.Combine(Application.dataPath, "Sample", "Rules", "pay.yaml"), "--retries", "0", "--port", Port);
        Assert.AreEqual(0, m_ExitCode, m_Output);
        Assert.AreEqual(200, GameValueReader.Get(new[] { "gems" })["gems"]);

        // 마지막에 slow를 골랐던 규칙이 끝난 뒤: 기본 응답으로 돌아와 있다
        yield return RunQaplay("mocks", "--port", Port);
        StringAssert.Contains("store = success  (", m_Output);

        yield return RunQaplay("run", Write("nomock.yaml", "- mock: { store: refund }\n"), "--retries", "0", "--port", Port);
        Assert.AreEqual(1, m_ExitCode, m_Output);
        StringAssert.Contains("-> unknown_mock: mock 'store' has no scenario \"refund\"; use success, fail, cancel, slow, duplicate", m_Output);
    }

    // 기기 지표는 OS에서 읽는다. 에디터에 붙기만 한 러너는 읽을 방법이 없고, 예산을 적은 규칙은 통과시키지 않는다
    [UnityTest]
    public IEnumerator Device_WithoutADeviceOrAnApp_IsNotAvailableAndFailsABudget()
    {
        yield return FreshScreen();
        yield return RunQaplay("device", "--seconds", "1", "--json", "--port", Port);
        Assert.AreEqual(0, m_ExitCode, m_Output);
        StringAssert.Contains("not available (needs --device for an Android device, or --app for a PC build", m_Output);
        Assert.IsNull(StdoutJson["cpu"]);

        yield return RunQaplay("run", Write("device.yaml", "- tapOn: { id: shop.buy }\n- assertDevice: { cpu: 400 }\n"), "--retries", "0", "--port", Port);
        Assert.AreEqual(1, m_ExitCode, m_Output);
        StringAssert.Contains("-> over_budget: cpu: not available here", m_Output);
        Assert.AreEqual(1100, Gold, "앞의 명령은 실행됐다");
    }

    // 누수가 없는 팝업은 통과하고, 통과해도 증가 추세와 기록 파일을 알려 준다
    [UnityTest]
    public IEnumerator Run_LeakCheckOnACleanPopup_PassesAndReportsTheGrowth()
    {
        yield return FreshScreen();
        yield return RunQaplay("run", Path.Combine(Application.dataPath, "Sample", "Rules", "leak-info.yaml"), "--json", "--port", Port);
        Assert.AreEqual(0, m_ExitCode, m_Output);
        StringAssert.IsMatch(@"memory: managed [+-]?[\d.]+ MB per repeat, native [+-]?[\d.]+ MB per repeat, history saved to .*leak-info\.memory\.csv", m_Output);
        Dictionary<string, object> rule = Map(((List<object>)StdoutJson["rules"])[0]);
        Assert.AreEqual("passed", rule["status"]);
        Assert.Less((double)Map(rule["growth"])["native"], RuleReader.DefaultLeakAllowMb);
        Assert.IsTrue(File.Exists((string)rule["memoryLog"]));
        UnityEngine.Debug.Log("[dbg] growth through the runner: " + Json.Write(rule["growth"]));
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
