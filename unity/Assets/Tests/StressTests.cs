using System.Collections;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Qaplay;
using Qaplay.Agent;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.TestTools;

// 입력 스트레스: 같은 버튼을 연달아 누르기(연타)와 여러 버튼을 같은 순간에 누르기(동시 클릭).
// 샘플 화면에 일부러 심은 버그 둘을 규칙이 잡아내는지, 멀쩡한 버튼에서는 누른 만큼만 처리되는지 본다
public class StressTests
{
    RuleResult m_Result;
    Dictionary<string, object> m_Response;
    Mouse m_Mouse;
    MouseNoise m_Noise;

    [TearDown]
    public void RemoveMouseNoise()
    {
        if (m_Noise != null) Object.Destroy(m_Noise.gameObject);
        if (m_Mouse != null && m_Mouse.added) InputSystem.RemoveDevice(m_Mouse);
        m_Noise = null;
        m_Mouse = null;
    }

    IEnumerator Run(string yaml)
    {
        SampleShop.Build();
        yield return null;
        m_Result = null;
        yield return RuleRunner.Run(RuleReader.Read(yaml), AgentSession.Local().Call, r => m_Result = r);
        Assert.IsNotNull(m_Result, "끝나면 결과를 알린다");
    }

    static string SampleFile(string path) => File.ReadAllText(Application.dataPath + "/Sample/" + path);

    IEnumerator Call(string cmd, string argsJson)
    {
        m_Response = null;
        yield return AgentSession.Local().Call(cmd, (Dictionary<string, object>)Json.Parse(argsJson), r => m_Response = r);
    }

    string ErrorCode => true.Equals(m_Response["ok"]) ? "ok" : (string)((Dictionary<string, object>)m_Response["error"])["code"];

    static object Value(string key) => GameValueReader.Get(new[] { key })[key];

    // ---- 연타 ----

    [UnityTest]
    public IEnumerator Repeat_ClicksExactlyThatManyTimes()
    {
        yield return Run(SampleFile("Rules/rapid-buy.yaml"));
        Assert.IsTrue(m_Result.passed, m_Result.ToString());
        Assert.AreEqual(700, Value("gold"));

        yield return Run("- tapOn: { id: shop.buy, repeat: 30 }");
        Assert.IsTrue(m_Result.passed, m_Result.ToString());
        Assert.AreEqual(1200 - 3000, Value("gold"), "30번 눌렀으면 30번 처리된다");
    }

    [UnityTest]
    public IEnumerator Repeat_WithDelay_WaitsBetweenTaps()
    {
        float start = Time.realtimeSinceStartup;
        yield return Run("- tapOn: { id: shop.buy, repeat: 3, delay: 200 }");

        Assert.IsTrue(m_Result.passed, m_Result.ToString());
        Assert.AreEqual(900, Value("gold"));
        Assert.GreaterOrEqual(Time.realtimeSinceStartup - start, 0.4f, "누름 사이 두 번을 0.2초씩 기다린다");
    }

    // 연타하는 내내 사람이 마우스를 움직여도 누른 횟수가 그대로여야 한다(한 번 누를 때의 보호가 끝까지 이어지는지)
    [UnityTest]
    public IEnumerator Repeat_WorksWhileMouseIsMoving()
    {
        m_Mouse = InputSystem.AddDevice<Mouse>();
        m_Noise = new GameObject("MouseNoise").AddComponent<MouseNoise>();
        m_Noise.mouse = m_Mouse;

        yield return Run("- tapOn: { id: shop.buy, repeat: 10, delay: 30 }");
        Assert.IsTrue(m_Result.passed, m_Result.ToString());
        Assert.AreEqual(200, Value("gold"));

        for (int frame = 0; frame < 10; frame++) yield return null;
        Assert.AreEqual(200, Value("gold"), "연타가 끝난 뒤 마우스 때문에 한 번 더 눌리면 안 된다");
    }

    // 심어 둔 버그 1: 선물 버튼은 응답이 올 때까지 자신을 막지 않는다
    [UnityTest]
    public IEnumerator Repeat_CatchesTheButtonThatHandlesEveryTap()
    {
        yield return Run(SampleFile("BuggyRules/rapid-gift.yaml"));

        Assert.IsFalse(m_Result.passed, "버그를 잡아 실패해야 한다");
        Assert.AreEqual(2, m_Result.index);
        Assert.AreEqual("value_mismatch", m_Result.code);
        Assert.AreEqual("gift.requests: expected 1, got 5", m_Result.message);

        // 한 번만 누르면 같은 규칙이 통과한다. 연타라서 드러난 버그다
        yield return Run("- tapOn: { id: shop.gift }\n- assertValue: { gift.requests: 1 }");
        Assert.IsTrue(m_Result.passed, m_Result.ToString());
    }

    // ---- 동시 클릭 ----

    // 심어 둔 버그 2: 판매 팝업은 확인과 취소가 같은 순간에 눌리면 둘 다 처리한다
    [UnityTest]
    public IEnumerator Together_CatchesThePopupThatHandlesBothButtons()
    {
        yield return Run(SampleFile("BuggyRules/together-sell.yaml"));

        Assert.IsFalse(m_Result.passed, "버그를 잡아 실패해야 한다");
        Assert.AreEqual(4, m_Result.index);
        Assert.AreEqual("value_mismatch", m_Result.code);
        Assert.AreEqual("popups: expected 0, got -1", m_Result.message);
        Assert.AreEqual(1500, Value("gold"), "취소도 눌렸는데 판매도 됐다");

        // 하나씩 누르면 같은 확인이 통과한다. 동시에 눌러서 드러난 버그다
        yield return Run(
            "- tapOn: { name: SellButton }\n" +
            "- tapOn: { name: CancelButton }\n" +
            "- assertNotVisible: { name: SellPopup }\n" +
            "- assertValue: { popups: 0, gold: 1200 }\n");
        Assert.IsTrue(m_Result.passed, m_Result.ToString());
    }

    [UnityTest]
    public IEnumerator Together_PressesEveryElementAtOnce()
    {
        SampleShop.Build();
        yield return null;
        yield return Call("tapTogether", "{\"selectors\":[{\"id\":\"shop.buy\"},{\"name\":\"InfoButton\"},{\"text\":\"REFRESH\"}],\"timeoutMs\":0}");

        Assert.AreEqual("ok", ErrorCode, Json.Write(m_Response));
        var points = (List<object>)((Dictionary<string, object>)m_Response["result"])["points"];
        Assert.AreEqual(3, points.Count);
        Assert.AreEqual(0.845, (double)((Dictionary<string, object>)points[0])["y"], 0.0005);
        Assert.AreEqual(1100, Value("gold"));
        Assert.AreEqual(1, Qaplay.UiFinder.Find(Qaplay.UGUI.UguiDumper.Dump(), new UiSelector { name = "InfoPopup" }).Count);
    }

    // 같은 요소를 두 번 적으면 같은 프레임에 두 번 눌린다. 연타(두 프레임에 한 번)보다 좁은 틈을 볼 때 쓴다
    [UnityTest]
    public IEnumerator Together_SameElementTwice_ClicksTwiceInOneFrame()
    {
        yield return Run("- tapTogether: [ { id: shop.buy }, { id: shop.buy } ]\n- assertValue: { gold: 1000 }");
        Assert.IsTrue(m_Result.passed, m_Result.ToString());
    }

    // 하나라도 누를 수 없으면 아무것도 누르지 않는다
    [UnityTest]
    public IEnumerator Together_PressesNothingUnlessAllAreTappable()
    {
        SampleShop.Build();
        yield return null;
        var cases = new[]
        {
            ("{\"selectors\":[{\"id\":\"shop.buy\"},{\"name\":\"Nope\"}],\"timeoutMs\":0}", "not_found"),
            ("{\"selectors\":[{\"id\":\"shop.buy\"},{\"name\":\"CoveredButton\"}],\"timeoutMs\":100}", "not_tappable"),
            ("{\"selectors\":[{\"id\":\"shop.buy\"},{\"name\":\"Item\"}],\"timeoutMs\":5000}", "ambiguous"),
            ("{\"selectors\":[{\"id\":\"shop.buy\"}]}", "bad_request"),
            ("{\"selectors\":[{\"id\":\"shop.buy\"},\"구매\"]}", "bad_request"),
            ("{\"selector\":{\"id\":\"shop.buy\"}}", "bad_request"),
        };
        foreach ((string args, string code) in cases)
        {
            yield return Call("tapTogether", args);
            Assert.AreEqual(code, ErrorCode, args);
        }
        for (int frame = 0; frame < 5; frame++) yield return null;
        Assert.AreEqual(1200, Value("gold"));
    }

    [UnityTest]
    public IEnumerator Tap_RejectsCountAndIntervalOutOfRange()
    {
        SampleShop.Build();
        yield return null;
        foreach (string extra in new[] { "\"count\":0", "\"count\":101", "\"count\":1.5", "\"intervalMs\":-1", "\"intervalMs\":10001" })
        {
            yield return Call("tap", "{\"selector\":{\"id\":\"shop.buy\"}," + extra + "}");
            Assert.AreEqual("bad_request", ErrorCode, extra);
        }
        Assert.AreEqual(1200, Value("gold"));
    }
}
