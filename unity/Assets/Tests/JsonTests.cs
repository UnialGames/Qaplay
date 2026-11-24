using System;
using System.Collections.Generic;
using NUnit.Framework;
using Qaplay;

public class JsonTests
{
    [Test]
    public void Parse_ReadsEveryKindOfValue()
    {
        var map = (Dictionary<string, object>)Json.Parse(
            " { \"s\" : \"구매\", \"n\": -12.5e1, \"i\": 7, \"t\": true, \"f\": false, \"z\": null,\n" +
            "   \"list\": [1, \"a\", [], {}], \"obj\": {\"inner\": {\"deep\": 1}} } ");

        Assert.AreEqual("구매", map["s"]);
        Assert.AreEqual(-125.0, map["n"]);
        Assert.AreEqual(7.0, map["i"]);
        Assert.AreEqual(true, map["t"]);
        Assert.AreEqual(false, map["f"]);
        Assert.IsTrue(map.ContainsKey("z"));
        Assert.IsNull(map["z"]);

        var list = (List<object>)map["list"];
        Assert.AreEqual(4, list.Count);
        Assert.AreEqual(1.0, list[0]);
        Assert.AreEqual("a", list[1]);
        Assert.AreEqual(0, ((List<object>)list[2]).Count);
        Assert.AreEqual(0, ((Dictionary<string, object>)list[3]).Count);

        var inner = (Dictionary<string, object>)((Dictionary<string, object>)map["obj"])["inner"];
        Assert.AreEqual(1.0, inner["deep"]);
    }

    [Test]
    public void Parse_ReadsStringEscapes()
    {
        Assert.AreEqual("a\"b\\c/d\n\t\r\b\f", Json.Parse("\"a\\\"b\\\\c\\/d\\n\\t\\r\\b\\f\""));
        Assert.AreEqual("가A", Json.Parse("\"\\uac00\\u0041\""));
        Assert.AreEqual("", Json.Parse("\"\""));
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase("{")]
    [TestCase("{\"a\":1,}")]
    [TestCase("{\"a\" 1}")]
    [TestCase("{a:1}")]
    [TestCase("[1,]")]
    [TestCase("[1 2]")]
    [TestCase("\"abc")]
    [TestCase("\"a\\x\"")]
    [TestCase("\"\\u12\"")]
    [TestCase("\"줄\n바꿈\"")]
    [TestCase("tru")]
    [TestCase("nul")]
    [TestCase("-")]
    [TestCase("1e999")]
    [TestCase("1 2")]
    [TestCase("{} x")]
    [TestCase("'a'")]
    public void Parse_RejectsBadInput(string text)
    {
        Assert.Throws<FormatException>(() => Json.Parse(text));
    }

    // 바깥에서 온 글자를 읽으므로, 깊게 겹친 입력이 스택을 넘치게 하면 안 된다
    [Test]
    public void Parse_RejectsDeepNestingInsteadOfCrashing()
    {
        Assert.Throws<FormatException>(() => Json.Parse(new string('[', 100000)));
        Assert.DoesNotThrow(() => Json.Parse(new string('[', 50) + new string(']', 50)));
    }

    [Test]
    public void Write_WritesEveryKindOfValue()
    {
        var value = new Dictionary<string, object>
        {
            ["s"] = "a\"b\\c\n",
            ["i"] = 1100,
            ["l"] = 5000000000L,
            ["f"] = 0.8450001f,
            ["d"] = 0.1,
            ["whole"] = 3.0,
            ["t"] = true,
            ["z"] = null,
            ["nan"] = float.NaN,
            ["list"] = new[] { "x", "y" },
            ["nested"] = new List<object> { new Dictionary<string, object>(), new List<object>() },
        };
        Assert.AreEqual(
            "{\"s\":\"a\\\"b\\\\c\\n\",\"i\":1100,\"l\":5000000000,\"f\":0.845,\"d\":0.1,\"whole\":3," +
            "\"t\":true,\"z\":null,\"nan\":null,\"list\":[\"x\",\"y\"],\"nested\":[{},[]]}",
            Json.Write(value));
    }

    [Test]
    public void Write_RejectsWhatJsonCannotHold()
    {
        Assert.Throws<ArgumentException>(() => Json.Write(new Dictionary<string, object> { ["o"] = new object() }));
    }

    // 쓴 것을 다시 읽으면 같은 값이 나온다
    [Test]
    public void WriteThenParse_RoundTrips()
    {
        string text = "따옴표 \" 역슬래시 \\ 줄\n탭\t제어\u0001 한글";
        Assert.AreEqual(text, Json.Parse(Json.Write(text)));
    }
}
