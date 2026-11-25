using System.Collections;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Qaplay;
using Qaplay.Agent;
using UnityEngine;
using UnityEngine.TestTools;

// 사전 검증기: 규칙을 실행하지 않고, 셀렉터를 샘플 화면의 첫 상태에 대어 본다
public class RuleCheckerTests
{
    List<RuleIssue> m_Issues;

    IEnumerator Check(string yaml)
    {
        SampleShop.Build();
        yield return null;
        m_Issues = null;
        yield return RuleChecker.Check(RuleReader.Read(yaml), AgentSession.Local().Call, issues => m_Issues = issues);
        Assert.IsNotNull(m_Issues, "끝나면 결과를 한 번 알린다");
    }

    string Summary => string.Join(" / ", m_Issues.ConvertAll(i => i.index + " " + i.kind + " " + i.count));

    [UnityTest]
    public IEnumerator Check_FindsSelectorsThatMatchNoneOrMany()
    {
        yield return Check(
            "- seed: 1\n" +                                                         // 1 확인할 것 없음
            "- tapOn: { id: shop.buy }\n" +                                         // 2 하나
            "- tapOn: 구매\n" +                                                      // 3 버튼과 그 안의 글자: 하나로 정해진다
            "- tapOn: { name: Item }\n" +                                           // 4 여럿
            "- assertVisible: { name: Item }\n" +                                   // 5 확인은 여럿이어도 된다
            "- tapOn: { name: Nope }\n" +                                           // 6 없음
            "- tapOn: { name: Text, within: { name: Item } }\n" +                   // 7 범위가 여럿
            "- assertVisible: { name: Text, within: { name: Nope } }\n" +           // 8 범위가 없음
            "- assertNotVisible: { name: Tosat }\n" +                               // 9 오타: 실행하면 조용히 통과할 명령
            "- assertValue: { gold: 999, nope: 1 }\n" +                             // 10 등록되지 않은 값
            "- assertValue: { gold: 999 }\n" +                                      // 11 값 비교는 하지 않는다
            "- tapOn: { name: Item, within: { name: ItemList }, index: 1 }\n");     // 12 하나

        Assert.AreEqual("4 many 6 / 6 none 0 / 7 many 0 / 8 none 0 / 9 none 0 / 10 unknown_key 0", Summary);
        Assert.AreEqual("command 4 (line 4): - tapOn: { name: Item } -> many: selector matched 6 elements",
            m_Issues[0].ToString());
        Assert.AreEqual("selector matched 0 elements", m_Issues[1].message);
        StringAssert.StartsWith("within: ", m_Issues[2].message);
        StringAssert.StartsWith("within: ", m_Issues[3].message);
        StringAssert.Contains("nope", m_Issues[5].message);

        // 아무것도 누르지 않았다
        Assert.AreEqual(1200, GameValueReader.Get(new[] { "gold" })["gold"]);
    }

    // 지금 화면 하나만 본다: 뒤에 나올 알림과 팝업 안의 요소는 none으로 나온다
    [UnityTest]
    public IEnumerator Check_SampleRuleFile_FlagsElementsOfLaterScreens()
    {
        yield return Check(File.ReadAllText(Application.dataPath + "/Sample/Rules/shop.yaml"));

        Assert.AreEqual("3 none 0 / 4 none 0 / 6 none 0 / 8 none 0 / 9 none 0", Summary);
    }

    [UnityTest]
    public IEnumerator Check_CleanRule_HasNoIssues()
    {
        yield return Check("- tapOn: { id: shop.buy }\n- assertVisible: { name: GoldLabel, text: \"1,200\" }\n- assertValue: { gold: 0 }\n");

        Assert.AreEqual(0, m_Issues.Count, Summary);
    }

    // 검증기가 기대는 find 응답의 resolves
    [UnityTest]
    public IEnumerator Find_ReportsHowTheSelectorResolves()
    {
        SampleShop.Build();
        yield return null;
        AgentSession session = AgentSession.Local();
        var cases = new[] { ("BuyButton", "one"), ("Item", "many"), ("Nope", "none") };
        foreach ((string name, string resolves) in cases)
        {
            Dictionary<string, object> response = null;
            var args = new Dictionary<string, object> { ["selector"] = new Dictionary<string, object> { ["name"] = name } };
            yield return session.Call("find", args, r => response = r);
            Assert.AreEqual(resolves, ((Dictionary<string, object>)response["result"])["resolves"], name);
        }
    }
}
