using System;
using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using Qaplay;

public class RuleReaderTests
{
    // docs/rules.md 1절의 예 그대로
    const string Example =
        "# 상점에서 구매하면 골드가 100 줄고 알림이 떴다 사라진다\n" +
        "tc: TC-012\n" +
        "name: 상점 구매\n" +
        "---\n" +
        "- seed: 12345\n" +
        "- tapOn: { id: shop.buy }\n" +
        "- assertVisible: { name: Toast }\n" +
        "- assertVisible: { name: GoldLabel, text: \"1,100\" }\n" +
        "- assertValue: { gold: 1100 }\n" +
        "- assertNotVisible: { name: Toast }\n" +
        "\n" +
        "# 안내 팝업을 열고 닫는다\n" +
        "- tapOn: { name: InfoButton }\n" +
        "- tapOn: { text: 닫기, within: { name: InfoPopup } }\n" +
        "- assertNotVisible: { name: InfoPopup }\n";

    static string Request(RuleStep step) => step.cmd + " " + Json.Write(step.args);

    [Test]
    public void Read_TurnsTheDocumentExampleIntoRequests()
    {
        Rule rule = RuleReader.Read(Example);

        Assert.AreEqual("TC-012", rule.tc);
        Assert.AreEqual("상점 구매", rule.name);
        Assert.AreEqual(9, rule.steps.Count);
        Assert.AreEqual("seed {\"seed\":12345}", Request(rule.steps[0]));
        Assert.AreEqual("tap {\"selector\":{\"id\":\"shop.buy\"},\"timeoutMs\":10000}", Request(rule.steps[1]));
        Assert.AreEqual("wait {\"selector\":{\"name\":\"Toast\"},\"timeoutMs\":10000,\"state\":\"visible\"}",
            Request(rule.steps[2]));
        Assert.AreEqual(
            "wait {\"selector\":{\"name\":\"GoldLabel\",\"text\":\"1,100\"},\"timeoutMs\":10000,\"state\":\"visible\"}",
            Request(rule.steps[3]));
        Assert.AreEqual("get {\"keys\":[\"gold\"]}", Request(rule.steps[4]));
        Assert.AreEqual(1100.0, rule.steps[4].expect["gold"]);
        Assert.AreEqual("wait {\"selector\":{\"name\":\"Toast\"},\"timeoutMs\":10000,\"state\":\"gone\"}",
            Request(rule.steps[5]));
        Assert.AreEqual(
            "tap {\"selector\":{\"text\":\"닫기\",\"within\":{\"name\":\"InfoPopup\"}},\"timeoutMs\":10000}",
            Request(rule.steps[7]));

        // 실패를 알릴 때 쓰는 줄 번호와 원문
        Assert.AreEqual(6, rule.steps[1].line);
        Assert.AreEqual("- tapOn: { id: shop.buy }", rule.steps[1].source);
        Assert.AreEqual(14, rule.steps[7].line);
        Assert.IsNull(rule.steps[1].expect);
    }

    [Test]
    public void Read_ReadsShorthandQuotesTimeoutAndIndex()
    {
        Rule rule = RuleReader.Read(
            "- tapOn: 구매\n" +
            "- tapOn: \"100\"\n" +
            "- tapOn: '그건 ''이것'''   # 작은따옴표\n" +
            "- tapOn: \"줄\\n바꿈 \\\"따옴표\\\"\"\n" +
            "- tapOn: { name: StartButton, timeout: 30000 }  # 오래 걸리는 화면\n" +
            "- assertVisible: { type: Button, name: Item, within: { name: ItemList }, index: 1, timeout: 0 }\n" +
            "- tapOn: 아이템 #3\n" +
            "- tapOn: http://example.com\n");

        Assert.AreEqual("tap {\"selector\":{\"text\":\"구매\"},\"timeoutMs\":10000}", Request(rule.steps[0]));
        Assert.AreEqual("100", Selector(rule.steps[1])["text"]);
        Assert.AreEqual("그건 '이것'", Selector(rule.steps[2])["text"]);
        Assert.AreEqual("줄\n바꿈 \"따옴표\"", Selector(rule.steps[3])["text"]);
        Assert.AreEqual("tap {\"selector\":{\"name\":\"StartButton\"},\"timeoutMs\":30000}", Request(rule.steps[4]));
        Assert.AreEqual(
            "wait {\"selector\":{\"type\":\"Button\",\"name\":\"Item\",\"within\":{\"name\":\"ItemList\"},\"index\":1}," +
            "\"timeoutMs\":0,\"state\":\"visible\"}",
            Request(rule.steps[5]));
        // 공백 뒤의 #은 YAML에서 주석이다
        Assert.AreEqual("아이템", Selector(rule.steps[6])["text"]);
        Assert.AreEqual("http://example.com", Selector(rule.steps[7])["text"]);
    }

    static Dictionary<string, object> Selector(RuleStep step) => (Dictionary<string, object>)step.args["selector"];

    [Test]
    public void Read_ReadsEveryKindOfExpectedValue()
    {
        RuleStep step = RuleReader.Read(
            "- assertValue: { gold: 1100, rate: -1.5, big: 1e3, name: 철수, alive: true, dead: false, pet: null, " +
            "\"my key\": \"a, b\", version: 1.2.3 }\n").steps[0];

        Assert.AreEqual("get", step.cmd);
        CollectionAssert.AreEqual(
            new[] { "gold", "rate", "big", "name", "alive", "dead", "pet", "my key", "version" },
            (List<string>)step.args["keys"]);
        Assert.AreEqual(1100.0, step.expect["gold"]);
        Assert.AreEqual(-1.5, step.expect["rate"]);
        Assert.AreEqual(1000.0, step.expect["big"]);
        Assert.AreEqual("철수", step.expect["name"]);
        Assert.AreEqual(true, step.expect["alive"]);
        Assert.AreEqual(false, step.expect["dead"]);
        Assert.IsTrue(step.expect.ContainsKey("pet"));
        Assert.IsNull(step.expect["pet"]);
        Assert.AreEqual("a, b", step.expect["my key"]);
        Assert.AreEqual("1.2.3", step.expect["version"]);
    }

    // 머리말이 없는 파일, 윈도우 줄바꿈, BOM, 들여 쓴 목록
    [Test]
    public void Read_AcceptsFilesAsEditorsSaveThem()
    {
        Rule rule = RuleReader.Read("﻿# 주석\r\n\r\n  - seed: -7\r\n  - tapOn: 구매\r\n");

        Assert.IsNull(rule.tc);
        Assert.IsNull(rule.name);
        Assert.AreEqual(2, rule.steps.Count);
        Assert.AreEqual("seed {\"seed\":-7}", Request(rule.steps[0]));
        Assert.AreEqual("구매", Selector(rule.steps[1])["text"]);

        // 머리말 없이 --- 로 시작해도 된다
        Assert.AreEqual(1, RuleReader.Read("---\n- tapOn: 구매\n").steps.Count);
    }

    [TestCase("- tapOn: a\n- tapp: b", "rule line 2: unknown command 'tapp'")]
    [TestCase("tc: T\ntitle: x\n---\n- tapOn: a", "rule line 2: unknown header field 'title'")]
    [TestCase("tc: T\ntc: U\n---\n- tapOn: a", "rule line 2: 'tc' is written twice")]
    [TestCase("tc: 12\n---\n- tapOn: a", "rule line 1: tc must be text")]
    [TestCase("tc: T\n- tapOn: a", "rule line 1: expected '- command: arguments'")]
    [TestCase("- tapOn: a\n---\n- tapOn: b", "rule line 1: commands go after '---'")]
    [TestCase("---\n- tapOn: a\n---", "rule line 3: only one '---'")]
    [TestCase("# 주석뿐\n", "rule has no commands")]
    [TestCase("tc: T\n---\n", "rule has no commands")]
    [TestCase("- tapOn:\n    id: shop.buy", "rule line 1: a value is missing (nested blocks are not supported")]
    [TestCase("- tapOn: a\n    timeout: 5", "rule line 2: nested blocks are not supported")]
    [TestCase("- tapOn: { id: shop.buy", "'{' must be closed with '}' on the same line")]
    [TestCase("- tapOn: { id: a } b", "unexpected text after the value")]
    [TestCase("- tapOn: { id: \"a\" name: b }", "expected ',' or '}'")]
    [TestCase("- tapOn: { id }", "expected 'key: value'")]
    [TestCase("- tapOn: { id:a }", "put a space after 'id:'")]
    [TestCase("- tapOn: { id: a, id: b }", "'id' is written twice")]
    [TestCase("- tapOn: { nmae: a }", "unknown selector field 'nmae'")]
    [TestCase("- tapOn: { within: { name: a, timeout: 5 } }", "unknown selector field 'timeout'")]
    [TestCase("- tapOn: { timeout: 500 }", "selector has no conditions")]
    [TestCase("- tapOn: {}", "selector has no conditions")]
    [TestCase("- tapOn: 100", "the argument must be text")]
    [TestCase("- tapOn: { text: true }", "text must be text")]
    [TestCase("- tapOn: { name: a, within: b }", "within must be { conditions }")]
    [TestCase("- tapOn: { name: a, index: -1 }", "index must be a whole number, 0 or greater")]
    [TestCase("- tapOn: { name: a, timeout: 1.5 }", "timeout must be a whole number")]
    [TestCase("- tapOn: \"닫는 따옴표 없음", "text is missing its closing \"")]
    [TestCase("- tapOn: '닫는 따옴표 없음", "text is missing its closing '")]
    [TestCase("- tapOn: \"a\\x\"", "bad escape")]
    [TestCase("- tapOn: [a, b]", "unsupported YAML")]
    [TestCase("- tapOn: *anchor", "unsupported YAML")]
    [TestCase("- tapOn: |", "unsupported YAML")]
    [TestCase("- tapOn: 가격: 100", "put quotes around text that contains ':'")]
    [TestCase("- seed: abc", "seed must be a whole number")]
    [TestCase("- seed: 1.5", "seed must be a whole number")]
    [TestCase("- seed: 99999999999", "seed must be a whole number")]
    [TestCase("- assertValue: gold", "assertValue needs { key: value }")]
    [TestCase("- assertValue: {}", "assertValue needs { key: value }")]
    [TestCase("- assertValue: { gold: { a: 1 } }", "the value of 'gold' must be")]
    [TestCase("- assertValue: { gold: }", "a value is missing")]
    [TestCase("-", "expected '- command: arguments'")]
    [TestCase("- tapOn", "expected 'key: value'")]
    public void Read_RejectsBadRulesWithTheReason(string yaml, string reason)
    {
        var e = Assert.Throws<FormatException>(() => RuleReader.Read(yaml));
        StringAssert.Contains(reason, e.Message);
    }

    // 깊게 겹친 입력이 스택을 넘치게 하면 안 된다
    [Test]
    public void Read_RejectsDeepNestingInsteadOfCrashing()
    {
        var deep = new StringBuilder("- tapOn: ");
        for (int i = 0; i < 100000; i++) deep.Append("{ within: ");
        Assert.Throws<FormatException>(() => RuleReader.Read(deep.ToString()));

        Assert.DoesNotThrow(() => RuleReader.Read(
            "- tapOn: { name: a, within: { name: b, within: { name: c, within: { name: d } } } }"));
    }
}
