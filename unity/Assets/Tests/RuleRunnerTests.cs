using System.Collections;
using System.IO;
using NUnit.Framework;
using Qaplay;
using Qaplay.Agent;
using UnityEngine;
using UnityEngine.TestTools;

// 규칙(YAML)을 읽어 샘플 화면에서 실행한다. 읽기 → 요청 → 에이전트 → 판정의 전체 경로다.
public class RuleRunnerTests
{
    RuleResult m_Result;

    IEnumerator Run(string yaml)
    {
        SampleShop.Build();
        yield return null;
        m_Result = null;
        int results = 0;
        yield return RuleRunner.Run(RuleReader.Read(yaml), AgentSession.Local().Call, r =>
        {
            results++;
            m_Result = r;
        });
        Assert.AreEqual(1, results, "끝나면 결과를 한 번 알린다");
    }

    static object Gold => GameValueReader.Get(new[] { "gold" })["gold"];

    // docs/rules.md 1절의 예와 같은 파일
    [UnityTest]
    public IEnumerator Run_SampleRuleFile_Passes()
    {
        yield return Run(File.ReadAllText(Application.dataPath + "/Sample/Rules/shop.yaml"));

        Assert.IsTrue(m_Result.passed, m_Result.ToString());
        Assert.AreEqual("passed", m_Result.ToString());
        // 통과만 한 것이 아니라 실제로 눌렀다
        Assert.AreEqual(1100, Gold);
    }

    [UnityTest]
    public IEnumerator Run_StopsAtTheFirstFailure()
    {
        yield return Run(
            "- tapOn: { id: shop.buy }\n" +
            "- assertValue: { gold: 999 }\n" +
            "- tapOn: { id: shop.buy }\n");

        Assert.IsFalse(m_Result.passed);
        Assert.AreEqual(2, m_Result.index);
        Assert.AreEqual("value_mismatch", m_Result.code);
        Assert.AreEqual("gold: expected 999, got 1100", m_Result.message);
        Assert.AreEqual(
            "failed at command 2 (line 2): - assertValue: { gold: 999 } -> value_mismatch: gold: expected 999, got 1100",
            m_Result.ToString());
        // 세 번째 명령은 실행하지 않았다
        Assert.AreEqual(1100, Gold);
    }

    [UnityTest]
    public IEnumerator Run_ReportsWhyACommandFailed()
    {
        var cases = new[]
        {
            ("- tapOn: { name: Nope, timeout: 0 }", "not_found"),
            ("- tapOn: { name: Item, timeout: 0 }", "ambiguous"),
            ("- tapOn: { name: CoveredButton, timeout: 0 }", "not_tappable"),
            ("- assertVisible: { name: Toast, timeout: 100 }", "timeout"),
            ("- assertNotVisible: { name: GoldLabel, timeout: 100 }", "timeout"),
            ("- assertValue: { nope: 1 }", "unknown_key"),
            // 숫자와 글자는 다른 값이다
            ("- assertValue: { gold: \"1200\" }", "value_mismatch"),
        };
        foreach ((string yaml, string code) in cases)
        {
            yield return Run(yaml);

            Assert.IsFalse(m_Result.passed, yaml);
            Assert.AreEqual(code, m_Result.code, yaml);
            Assert.AreEqual(1, m_Result.index, yaml);
            Assert.AreEqual(yaml, m_Result.step.source);
            Assert.IsNotEmpty(m_Result.message, yaml);
        }
        Assert.AreEqual("gold: expected \"1200\", got 1200", m_Result.message);
    }

    // 에디터 창이 쓰는 길: 게임 밖에서 시작한 실행을 숨은 게임오브젝트(AgentHost)가 게임 프레임에 맞춰 돌린다
    [UnityTest]
    public IEnumerator AgentHost_RunsARuleStartedOutsideTheGameLoop()
    {
        SampleShop.Build();
        yield return null;
        RuleResult result = null;
        AgentHost.Run(RuleRunner.Run(RuleReader.Read("- tapOn: { id: shop.buy }\n- assertValue: { gold: 1100 }"),
            AgentSession.Local().Call, r => result = r));
        for (int frame = 0; frame < 300 && result == null; frame++) yield return null;

        Assert.IsNotNull(result, "300프레임 안에 끝나야 한다");
        Assert.IsTrue(result.passed, result.ToString());
    }

    // 여러 값을 한 번에 확인하고, 다른 것만 알린다
    [UnityTest]
    public IEnumerator Run_AssertValue_ListsOnlyTheKeysThatDiffer()
    {
        GameValues.Register("test.name", () => "철수");
        GameValues.Register("test.vip", () => true);
        try
        {
            yield return Run("- assertValue: { gold: 1200, test.name: 철수, test.vip: true }");
            Assert.IsTrue(m_Result.passed, m_Result.ToString());

            yield return Run("- assertValue: { gold: 1200, test.name: 영희, test.vip: false }");
            Assert.AreEqual("test.name: expected \"영희\", got \"철수\"; test.vip: expected false, got true", m_Result.message);
        }
        finally
        {
            GameValues.Unregister("test.name");
            GameValues.Unregister("test.vip");
        }
    }

    // 규칙은 한 게임에서 차례로 돈다. 샘플 규칙은 reset으로 처음 상태에서 시작하므로 어떤 순서로 돌아도 같은 결과다
    // (예전에는 구매 연타가 골드를 바꿔 놓아 뒤에 돈 상점 구매가 실패했다)
    [UnityTest]
    public IEnumerator SampleRules_PassInAnyOrder_BecauseEachStartsWithReset()
    {
        SampleShop.Build();
        yield return null;
        string Sample(string name) => File.ReadAllText(Application.dataPath + "/Sample/Rules/" + name + ".yaml");
        foreach (string name in new[] { "rapid-buy", "shop", "pay", "rapid-buy", "layout", "shop" })
        {
            RuleResult result = null;
            yield return RuleRunner.Run(RuleReader.Read(Sample(name)), AgentSession.Local().Call, r => result = r);
            Assert.IsTrue(result.passed, name + ": " + result);
        }
    }

    [UnityTest]
    public IEnumerator Reset_GoesBackToTheStart_AndSaysWhatIsMissing()
    {
        yield return Run("- tapOn: { id: shop.buy, repeat: 3 }\n- reset: all\n- assertValue: { gold: 1200 }\n- reset: shop\n- reset: [shop]\n");
        Assert.IsTrue(m_Result.passed, m_Result.ToString());

        yield return Run("- reset: inventory\n");
        Assert.AreEqual("unknown_reset", m_Result.code);
        StringAssert.Contains("registered: shop", m_Result.message);

        // 게임이 아무것도 등록하지 않았으면 무엇을 해야 하는지 알린다
        GameReset.Unregister("shop");
        try
        {
            RuleResult result = null;
            yield return RuleRunner.Run(RuleReader.Read("- reset: all\n"), AgentSession.Local().Call, r => result = r);
            Assert.AreEqual("no_reset", result.code);
            StringAssert.Contains("GameReset.Register", result.message);
        }
        finally
        {
            SampleShop.Build(); // 다시 등록한다
        }
        Assert.Throws<System.FormatException>(() => RuleReader.Read("- reset: []\n"));
    }
}
