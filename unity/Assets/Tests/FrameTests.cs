using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using NUnit.Framework;
using Qaplay;
using Qaplay.Agent;
using UnityEngine;
using UnityEngine.TestTools;

// frames 명령과 규칙의 assertFrameTime. 끊김은 메인 스레드를 잠깐 막아서 만든다.
public class FrameTests
{
    Dictionary<string, object> m_Frames;
    RuleResult m_Result;

    IEnumerator TakeFrames(int? hitchMs = null)
    {
        var args = new Dictionary<string, object>();
        if (hitchMs != null) args["hitchMs"] = hitchMs.Value;
        Dictionary<string, object> response = null;
        yield return AgentSession.Local().Call("frames", args, r => response = r);
        Assert.AreEqual(true, response["ok"], Json.Write(response));
        m_Frames = (Dictionary<string, object>)response["result"];
    }

    double Number(string key) => Convert.ToDouble(m_Frames[key]);

    static IEnumerator Frames(int count)
    {
        for (int n = 0; n < count; n++) yield return null;
    }

    IEnumerator RunOnFreshScreen(string yaml)
    {
        SampleShop.Build();
        yield return Frames(3); // 화면을 처음 그리는 프레임은 재는 구간에 넣지 않는다
        m_Result = null;
        yield return RuleRunner.Run(RuleReader.Read(yaml), AgentSession.Local().Call, r => m_Result = r);
    }

    [UnityTest]
    public IEnumerator Frames_SummarizesTheFramesSinceThePreviousRequest()
    {
        yield return TakeFrames(); // 여기부터 잰다
        yield return Frames(30);
        yield return TakeFrames();

        Assert.That(Number("count"), Is.InRange(28, 32));
        Assert.Greater(Number("averageMs"), 0);
        Assert.GreaterOrEqual(Number("p99Ms"), Number("averageMs"));
        Assert.GreaterOrEqual(Number("maxMs"), Number("p99Ms"));
        // 평균 × 프레임 수 = 잰 시간
        Assert.AreEqual(Number("seconds"), Number("averageMs") * Number("count") / 1000.0, 0.02);
        Assert.AreEqual(50.0, m_Frames["hitchMs"]);

        // 바로 다시 읽으면 그 사이의 프레임뿐이다
        yield return TakeFrames();
        Assert.LessOrEqual(Number("count"), 1);
    }

    [UnityTest]
    public IEnumerator Frames_CountsAFrameLongerThanTheThresholdAsAHitch()
    {
        yield return TakeFrames();
        yield return Frames(5);
        Thread.Sleep(120); // 이 프레임이 120ms 넘게 걸린다
        yield return Frames(5);
        yield return TakeFrames();
        Assert.GreaterOrEqual(Number("hitches"), 1);
        Assert.GreaterOrEqual(Number("maxMs"), 120.0);

        // 기준을 올리면 같은 길이의 프레임을 끊김으로 세지 않는다
        Thread.Sleep(120);
        yield return Frames(5);
        yield return TakeFrames(1000);
        Assert.AreEqual(0.0, m_Frames["hitches"]);
        Assert.GreaterOrEqual(Number("maxMs"), 120.0);
        Assert.AreEqual(1000.0, m_Frames["hitchMs"]);
    }

    [UnityTest]
    public IEnumerator Frames_RejectsABadThreshold()
    {
        Dictionary<string, object> response = null;
        yield return AgentSession.Local().Call("frames", new Dictionary<string, object> { ["hitchMs"] = 0 }, r => response = r);
        Assert.AreEqual("bad_request", ((Dictionary<string, object>)response["error"])["code"]);
    }

    // 샘플에 심은 끊김: 정렬 버튼은 메인 스레드를 0.15초 막는다
    [UnityTest]
    public IEnumerator AssertFrameTime_CatchesTheHitchInTheSampleSortButton()
    {
        yield return RunOnFreshScreen(System.IO.File.ReadAllText(Application.dataPath + "/Sample/BuggyRules/hitch-sort.yaml"));

        Assert.IsFalse(m_Result.passed);
        Assert.AreEqual("over_budget", m_Result.code);
        Assert.AreEqual(2, m_Result.index);
        StringAssert.IsMatch(@"^hitches: \d+ frame\(s\) over 50 ms \(longest \d{3,}\.\d ms\), budget 0$", m_Result.message);
        Assert.GreaterOrEqual(Convert.ToDouble(m_Result.frames["maxMs"]), 150.0);
    }

    [UnityTest]
    public IEnumerator AssertFrameTime_PassesInsideTheBudget()
    {
        yield return RunOnFreshScreen(
            "- tapOn: { name: InfoButton }\n" +
            "- assertFrameTime: { average: 1000, p99: 5000, hitches: 1000 }\n");

        Assert.IsTrue(m_Result.passed, m_Result.ToString());
        Assert.Greater(Convert.ToDouble(m_Result.frames["count"]), 0, "규칙의 시작부터 잰다");

        // 넘은 지표만 알린다
        yield return RunOnFreshScreen(
            "- tapOn: { name: InfoButton }\n" +
            "- assertFrameTime: { average: 0.001, hitches: 1000 }\n");
        Assert.AreEqual("over_budget", m_Result.code);
        StringAssert.IsMatch(@"^average: \d+\.\d ms, budget 0\.001 ms$", m_Result.message);
    }

    // 끊김의 기준을 규칙에서 바꿀 수 있고, 확인할 때마다 그 앞의 확인 뒤부터만 본다
    [UnityTest]
    public IEnumerator AssertFrameTime_UsesItsOwnThresholdAndStartsOverAfterEachCheck()
    {
        yield return RunOnFreshScreen(
            "- tapOn: { name: SortButton }\n" +
            "- assertFrameTime: { hitches: 0, hitchOver: 1000 }\n" +   // 0.15초는 1초 기준으로는 끊김이 아니다
            "- tapOn: { name: InfoButton }\n" +
            "- assertFrameTime: { hitches: 0, hitchOver: 100 }\n");    // 정렬의 0.15초는 앞 구간의 일이다

        Assert.IsTrue(m_Result.passed, m_Result.ToString());
    }
}
