using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Qaplay;
using Qaplay.Agent;
using UnityEngine;
using UnityEngine.TestTools;

// mock 명령, 규칙의 mock, 그리고 SDK 호출을 세는 PluginCalls. 샘플 상점의 가짜 결제(SampleStore.cs)로 확인한다.
public class MockTests
{
    Dictionary<string, object> m_Response;
    RuleResult m_Result;

    IEnumerator Send(Dictionary<string, object> args)
    {
        m_Response = null;
        yield return AgentSession.Local().Call("mock", args, r => m_Response = r);
    }

    Dictionary<string, object> Store =>
        (Dictionary<string, object>)((Dictionary<string, object>)((Dictionary<string, object>)m_Response["result"])["mocks"])["store"];

    static Dictionary<string, object> Set(string name, object scenario) =>
        new Dictionary<string, object> { ["set"] = new Dictionary<string, object> { [name] = scenario } };

    static object Value(string key) => GameValueReader.Get(new[] { key })[key];

    IEnumerator RunOnFreshScreen(string yaml)
    {
        SampleShop.Build();
        yield return null;
        m_Result = null;
        yield return RuleRunner.Run(RuleReader.Read(yaml), AgentSession.Local().Call, r => m_Result = r);
    }

    static string SampleFile(string path) => System.IO.File.ReadAllText(Application.dataPath + "/Sample/" + path);

    [UnityTest]
    public IEnumerator Mock_ListsWhatTheGameRegisteredAndPicksAScenario()
    {
        SampleShop.Build();
        yield return null;

        // 인자 없이: 바꾸지 않고 보여 주기만 한다. 등록할 때의 첫 번째가 기본값이다
        yield return Send(null);
        Assert.AreEqual("success", Store["scenario"]);
        CollectionAssert.AreEqual(new[] { "success", "fail", "cancel", "slow", "duplicate" }, (List<object>)Store["scenarios"]);

        yield return Send(Set("store", "cancel"));
        Assert.AreEqual("cancel", Store["scenario"]);
        Assert.AreEqual("cancel", Mocks.Scenario("store"), "게임의 Mock이 읽는 값이 바뀐다");

        yield return Send(new Dictionary<string, object> { ["reset"] = true });
        Assert.AreEqual("success", Mocks.Scenario("store"));
    }

    [UnityTest]
    public IEnumerator Mock_RejectsUnknownNamesAndScenariosWithoutChangingAnything()
    {
        SampleShop.Build();
        yield return null;

        // 하나라도 틀리면 맞는 것도 바꾸지 않는다
        yield return Send(new Dictionary<string, object>
            { ["set"] = new Dictionary<string, object> { ["store"] = "cancel", ["stor"] = "cancel" } });
        var error = (Dictionary<string, object>)m_Response["error"];
        Assert.AreEqual("unknown_mock", error["code"]);
        StringAssert.Contains("no mock named 'stor'; registered: store", (string)error["message"]);
        Assert.AreEqual("success", Mocks.Scenario("store"));

        yield return Send(Set("store", "cancle"));
        error = (Dictionary<string, object>)m_Response["error"];
        Assert.AreEqual("unknown_mock", error["code"]);
        StringAssert.Contains("mock 'store' has no scenario \"cancle\"; use success, fail, cancel, slow, duplicate", (string)error["message"]);
        Assert.AreEqual("success", Mocks.Scenario("store"));
    }

    // 샘플 규칙: 응답을 바꿔 가며 결제됐을 때만 보석이 들어오는지 본다
    [UnityTest]
    public IEnumerator Rule_PicksStoreResponsesAndSeesOnlyPaidPurchasesGrantGems()
    {
        yield return RunOnFreshScreen(SampleFile("Rules/pay.yaml"));

        Assert.IsTrue(m_Result.passed, m_Result.ToString());
        Assert.AreEqual(200, Value("gems"));
        Assert.AreEqual(4, Value("plugin.store.buy.started"));
        Assert.AreEqual(2, Value("plugin.store.buy.succeeded"));
        Assert.AreEqual(2, Value("plugin.store.buy.failed"), "취소와 실패");
        // 늦은 응답(2초)은 걸린 시간에 그대로 남는다
        Assert.Greater((double)Value("plugin.store.buy.lastMs"), 1900.0);
        Assert.AreEqual("success", Mocks.Scenario("store"), "규칙이 끝나면 기본 응답으로 돌려놓는다");
    }

    // 샘플에 심은 버그: 결제 완료가 두 번 오면 보석을 두 번 준다
    [UnityTest]
    public IEnumerator Rule_CatchesTheDoubleGrantOnADuplicateCallback()
    {
        yield return RunOnFreshScreen(SampleFile("BuggyRules/pay-duplicate.yaml"));

        Assert.IsFalse(m_Result.passed);
        Assert.AreEqual("value_mismatch", m_Result.code);
        Assert.AreEqual("gems: expected 100, got 200", m_Result.message);
        // 호출은 한 번인데 콜백이 두 번 왔다: 기다리는 호출 수가 음수다
        Assert.AreEqual(-1, Value("plugin.store.buy.pending"));
        Assert.AreEqual("success", Mocks.Scenario("store"), "실패한 규칙도 끝나면 돌려놓는다");
    }

    [UnityTest]
    public IEnumerator Rule_WithAMockTheGameDoesNotHave_FailsAtThatCommand()
    {
        yield return RunOnFreshScreen("- mock: { login: fail }\n- tapOn: { name: PayButton }\n");

        Assert.AreEqual("unknown_mock", m_Result.code);
        Assert.AreEqual(1, m_Result.index);
        Assert.AreEqual(0, Value("plugin.store.buy.started"), "뒤의 명령은 실행하지 않았다");
    }

    [Test]
    public void PluginCalls_CountCallsCallbacksAndTime()
    {
        PluginCalls.Track("test.sdk");
        try
        {
            Assert.AreEqual(0, Value("plugin.test.sdk.started"), "부르기 전에도 읽힌다");
            Assert.AreEqual(0, Value("plugin.test.sdk.pending"));

            PluginCalls.Started("test.sdk");
            Assert.AreEqual(1, Value("plugin.test.sdk.pending"), "콜백을 기다리는 중");
            System.Threading.Thread.Sleep(20);
            PluginCalls.Finished("test.sdk");
            Assert.AreEqual(1, Value("plugin.test.sdk.succeeded"));
            Assert.AreEqual(0, Value("plugin.test.sdk.pending"));
            Assert.GreaterOrEqual((double)Value("plugin.test.sdk.lastMs"), 15.0);

            PluginCalls.Started("test.sdk");
            PluginCalls.Finished("test.sdk", false);
            Assert.AreEqual(1, Value("plugin.test.sdk.failed"));
            Assert.AreEqual(2, Value("plugin.test.sdk.started"));

            // 다시 Track하면 0부터 센다
            PluginCalls.Track("test.sdk");
            Assert.AreEqual(0, Value("plugin.test.sdk.started"));
        }
        finally
        {
            foreach (string suffix in new[] { "started", "succeeded", "failed", "pending", "lastMs" })
                GameValues.Unregister("plugin.test.sdk." + suffix);
        }
    }
}
