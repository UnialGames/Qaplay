using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Qaplay;
using Qaplay.Agent;
using UnityEngine.TestTools;

// 러너가 보낼 요청 JSON을 그대로 넣고, 돌아온 응답 JSON을 확인한다. 소켓만 빠진 전체 경로다.
public class AgentTests
{
    AgentSession m_Session;
    Dictionary<string, object> m_Response;

    [SetUp]
    public void NewSession() => m_Session = new AgentSession();

    IEnumerator Send(string json)
    {
        m_Response = null;
        int responses = 0;
        yield return m_Session.Handle(json, text =>
        {
            responses++;
            m_Response = (Dictionary<string, object>)Json.Parse(text);
        });
        Assert.AreEqual(1, responses, "요청 하나에 응답은 한 번");
        Assert.AreEqual("res", m_Response["type"]);
    }

    IEnumerator Hello() =>
        Send("{\"type\":\"req\",\"id\":1,\"cmd\":\"hello\",\"args\":{\"protocol\":{\"min\":1,\"max\":1}}}");

    Dictionary<string, object> Result
    {
        get
        {
            Assert.AreEqual(true, m_Response["ok"], Json.Write(m_Response));
            return (Dictionary<string, object>)m_Response["result"];
        }
    }

    Dictionary<string, object> Error
    {
        get
        {
            Assert.AreEqual(false, m_Response["ok"], Json.Write(m_Response));
            return (Dictionary<string, object>)m_Response["error"];
        }
    }

    Dictionary<string, object> ErrorData => (Dictionary<string, object>)Error["data"];

    static Dictionary<string, object> Map(object value) => (Dictionary<string, object>)value;
    static List<object> List(object value) => (List<object>)value;

    // 누르고 → 기다리고 → 확인하는 테스트 한 편을 요청 JSON만으로 진행한다
    [UnityTest]
    public IEnumerator Requests_DriveTheSampleShopEndToEnd()
    {
        SampleShop.Build();
        yield return null;

        yield return Hello();
        Assert.AreEqual(1.0, m_Response["id"]);
        Assert.AreEqual(1.0, Result["protocol"]);
        Assert.AreEqual(QaplayInfo.AgentVersion, Map(Result["agent"])["version"]);
        CollectionAssert.AreEqual(new[] { "ugui" }, List(Result["ui"]));
        CollectionAssert.AreEqual(new[] { "hello", "dump", "find", "tap", "wait", "get" }, List(Result["commands"]));
        Assert.Greater((double)Map(Result["screen"])["width"], 0);

        yield return Send("{\"type\":\"req\",\"id\":2,\"cmd\":\"dump\"}");
        Assert.AreEqual(2.0, m_Response["id"]);
        Assert.Greater((double)Map(Result["screen"])["height"], 0);
        Dictionary<string, object> canvas = Map(List(Result["roots"])[0]);
        Assert.AreEqual("Canvas", canvas["name"]);
        Assert.AreEqual(9, List(canvas["children"]).Count);

        yield return Send("{\"type\":\"req\",\"id\":3,\"cmd\":\"find\",\"args\":{\"selector\":{\"type\":\"Button\",\"text\":\"구매\"}}}");
        Dictionary<string, object> buy = Map(List(Result["matches"])[0]);
        Assert.AreEqual(1, List(Result["matches"]).Count);
        Assert.AreEqual("Canvas/BuyButton", buy["path"]);
        Assert.AreEqual("shop.buy", buy["id"]);
        Assert.AreEqual(true, buy["interactable"]);
        Assert.AreEqual(0.35, (double)Map(buy["rect"])["x"], 0.0005);
        Assert.IsFalse(buy.ContainsKey("children"));

        // 명세의 tap 예시 그대로: 고유 ID로 누른다
        yield return Send("{\"type\":\"req\",\"id\":4,\"cmd\":\"tap\",\"args\":{\"selector\":{\"id\":\"shop.buy\"}}}");
        Assert.AreEqual(0.5, (double)Map(Result["point"])["x"], 0.0005);
        Assert.AreEqual(0.845, (double)Map(Result["point"])["y"], 0.0005);

        yield return Send("{\"type\":\"req\",\"id\":5,\"cmd\":\"wait\",\"args\":{" +
                          "\"selector\":{\"name\":\"GoldLabel\",\"text\":\"1,100\"},\"state\":\"visible\",\"timeoutMs\":5000}}");
        Assert.GreaterOrEqual((double)Result["elapsedMs"], 0);

        yield return Send("{\"type\":\"req\",\"id\":6,\"cmd\":\"get\",\"args\":{\"keys\":[\"gold\"]}}");
        Assert.AreEqual(1100.0, Map(Result["values"])["gold"]);

        // keys를 생략하면 등록된 값 전체
        yield return Send("{\"type\":\"req\",\"id\":7,\"cmd\":\"get\"}");
        Assert.IsTrue(Map(Result["values"]).ContainsKey("gold"));

        // within과 index도 요청에서 그대로 전달된다
        yield return Send("{\"type\":\"req\",\"id\":8,\"cmd\":\"find\",\"args\":{\"selector\":" +
                          "{\"type\":\"Text\",\"within\":{\"name\":\"BuyButton\"},\"index\":0}}}");
        Assert.AreEqual("Canvas/BuyButton/Text", Map(List(Result["matches"])[0])["path"]);
    }

    [UnityTest]
    public IEnumerator Errors_ComeBackWithCodeAndData()
    {
        SampleShop.Build();
        yield return null;
        yield return Hello();

        yield return Send("{\"type\":\"req\",\"id\":2,\"cmd\":\"fly\"}");
        Assert.AreEqual("unknown_command", Error["code"]);
        Assert.AreEqual(2.0, m_Response["id"]);
        Assert.IsInstanceOf<string>(Error["message"]);
        Assert.IsFalse(Error.ContainsKey("data"));

        yield return Send("{\"type\":\"req\",\"id\":3,\"cmd\":\"tap\",\"args\":{\"selector\":{\"name\":\"Nobody\"}}}");
        Assert.AreEqual("not_found", Error["code"]);

        yield return Send("{\"type\":\"req\",\"id\":4,\"cmd\":\"tap\",\"args\":{\"selector\":{\"type\":\"Button\"}}}");
        Assert.AreEqual("ambiguous", Error["code"]);
        Assert.AreEqual(4.0, ErrorData["count"]);
        Assert.AreEqual(4, List(ErrorData["matches"]).Count);
        Assert.AreEqual("Canvas/BuyButton", Map(List(ErrorData["matches"])[0])["path"]);

        yield return Send("{\"type\":\"req\",\"id\":5,\"cmd\":\"tap\",\"args\":{\"selector\":{\"name\":\"CoveredButton\"}}}");
        Assert.AreEqual("not_tappable", Error["code"]);
        Assert.AreEqual("occluded", ErrorData["reason"]);

        yield return Send("{\"type\":\"req\",\"id\":6,\"cmd\":\"wait\",\"args\":{" +
                          "\"selector\":{\"name\":\"Nobody\"},\"state\":\"visible\",\"timeoutMs\":100}}");
        Assert.AreEqual("timeout", Error["code"]);
        Assert.AreEqual(0.0, ErrorData["count"]);

        yield return Send("{\"type\":\"req\",\"id\":7,\"cmd\":\"get\",\"args\":{\"keys\":[\"gold\",\"gems\"]}}");
        Assert.AreEqual("unknown_key", Error["code"]);
        CollectionAssert.AreEqual(new[] { "gems" }, List(ErrorData["keys"]));

        // 오류가 난 뒤에도 다음 요청은 정상 처리된다
        yield return Send("{\"type\":\"req\",\"id\":8,\"cmd\":\"get\",\"args\":{\"keys\":[\"gold\"]}}");
        Assert.AreEqual(1200.0, Map(Result["values"])["gold"]);
    }

    static readonly string[] BadRequests =
    {
        "{\"type\":\"req\",\"id\":9}",                                                              // cmd 없음
        "{\"type\":\"res\",\"id\":9,\"cmd\":\"dump\"}",                                             // 요청이 아님
        "{\"type\":\"req\",\"id\":9,\"cmd\":\"find\",\"args\":[]}",                                 // args가 객체가 아님
        "{\"type\":\"req\",\"id\":9,\"cmd\":\"find\"}",                                             // selector 없음
        "{\"type\":\"req\",\"id\":9,\"cmd\":\"find\",\"args\":{\"selector\":{}}}",                  // 조건 없는 셀렉터
        "{\"type\":\"req\",\"id\":9,\"cmd\":\"find\",\"args\":{\"selector\":{\"name\":3}}}",        // 종류가 틀림
        "{\"type\":\"req\",\"id\":9,\"cmd\":\"find\",\"args\":{\"selector\":{\"name\":\"a\",\"index\":1.5}}}",
        "{\"type\":\"req\",\"id\":9,\"cmd\":\"find\",\"args\":{\"selector\":{\"name\":\"a\",\"within\":\"b\"}}}",
        "{\"type\":\"req\",\"id\":9,\"cmd\":\"find\",\"args\":{\"selector\":{\"id\":7}}}",
        "{\"type\":\"req\",\"id\":9,\"cmd\":\"wait\",\"args\":{\"selector\":{\"name\":\"a\"},\"timeoutMs\":100}}",          // state 없음
        "{\"type\":\"req\",\"id\":9,\"cmd\":\"wait\",\"args\":{\"selector\":{\"name\":\"a\"},\"state\":\"visible\"}}",      // timeoutMs 없음
        "{\"type\":\"req\",\"id\":9,\"cmd\":\"wait\",\"args\":{\"selector\":{\"name\":\"a\"},\"state\":\"shiny\",\"timeoutMs\":100}}",
        "{\"type\":\"req\",\"id\":9,\"cmd\":\"get\",\"args\":{\"keys\":\"gold\"}}",
        "{\"type\":\"req\",\"id\":9,\"cmd\":\"get\",\"args\":{\"keys\":[1]}}",
    };

    [UnityTest]
    public IEnumerator BadRequests_AreAnsweredNotDropped()
    {
        SampleShop.Build();
        yield return null;
        yield return Hello();

        foreach (string request in BadRequests)
        {
            yield return Send(request);
            Assert.AreEqual("bad_request", Error["code"], request);
            Assert.AreEqual(9.0, m_Response["id"], request);
        }
    }

    [UnityTest]
    public IEnumerator Hello_MustComeFirstAndAgreeOnVersion()
    {
        yield return Send("{\"type\":\"req\",\"id\":1,\"cmd\":\"dump\"}");
        Assert.AreEqual("hello_required", Error["code"]);

        yield return Send("{\"type\":\"req\",\"id\":2,\"cmd\":\"hello\",\"args\":{\"protocol\":{\"min\":2,\"max\":3}}}");
        Assert.AreEqual("unsupported_protocol", Error["code"]);
        Assert.AreEqual(1.0, ErrorData["min"]);
        Assert.AreEqual(1.0, ErrorData["max"]);

        // 버전이 맞지 않았으면 hello가 끝난 것이 아니다
        yield return Send("{\"type\":\"req\",\"id\":3,\"cmd\":\"dump\"}");
        Assert.AreEqual("hello_required", Error["code"]);

        yield return Send("{\"type\":\"req\",\"id\":4,\"cmd\":\"hello\",\"args\":{}}");
        Assert.AreEqual("bad_request", Error["code"]);

        // 러너가 더 넓은 범위를 지원해도 겹치는 버전이 있으면 된다
        yield return Send("{\"type\":\"req\",\"id\":5,\"cmd\":\"hello\",\"args\":{\"protocol\":{\"min\":1,\"max\":4}}}");
        Assert.AreEqual(1.0, Result["protocol"]);

        yield return Send("{\"type\":\"req\",\"id\":6,\"cmd\":\"dump\"}");
        Assert.IsTrue(Result.ContainsKey("roots"));
    }

    // 응답할 수 없는 프레임은 예외로 알린다. 부르는 쪽(소켓)은 연결을 끊는다
    [TestCase("not json")]
    [TestCase("[1,2]")]
    [TestCase("{\"id\":1,\"cmd\":\"dump\"}")]
    [TestCase("{\"type\":\"req\",\"cmd\":\"dump\"}")]
    [TestCase("{\"type\":\"req\",\"id\":\"1\",\"cmd\":\"dump\"}")]
    [TestCase("{\"type\":\"req\",\"id\":0,\"cmd\":\"dump\"}")]
    [TestCase("{\"type\":\"req\",\"id\":1.5,\"cmd\":\"dump\"}")]
    public void UnanswerableFrames_Throw(string frame)
    {
        Assert.Throws<FormatException>(() => new AgentSession().Handle(frame, _ => Assert.Fail("응답하면 안 됨")));
    }
}
