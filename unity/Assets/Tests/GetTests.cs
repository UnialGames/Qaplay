using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Qaplay;
using Qaplay.Injection;
using Qaplay.UGUI;
using UnityEngine;
using UnityEngine.TestTools;

public class GetTests
{
    [SetUp]
    public void Register()
    {
        GameValues.Register("test.level", () => 7);
        GameValues.Register("test.name", () => "kim");
        GameValues.Register("test.vip", () => true);
        GameValues.Register("test.guild", () => null);
    }

    [TearDown]
    public void Unregister()
    {
        foreach (string key in new[] { "test.level", "test.name", "test.vip", "test.guild", "test.position" })
            GameValues.Unregister(key);
    }

    [Test]
    public void Get_ReturnsRequestedValues()
    {
        Dictionary<string, object> values = GameValues.Get(new[] { "test.level", "test.name" });
        Assert.AreEqual(2, values.Count);
        Assert.AreEqual(7, values["test.level"]);
        Assert.AreEqual("kim", values["test.name"]);
    }

    // keys를 생략하면 등록된 값을 모두 돌려준다. 어떤 값을 읽을 수 있는지 조회할 때 쓴다
    [Test]
    public void Get_WithoutKeys_ReturnsEverything()
    {
        Dictionary<string, object> values = GameValues.Get();
        Assert.AreEqual(true, values["test.vip"]);
        Assert.IsTrue(values.ContainsKey("test.guild"));
        Assert.IsNull(values["test.guild"]);
        Assert.GreaterOrEqual(values.Count, 4);
    }

    [Test]
    public void Get_ReadsCurrentValueEachTime()
    {
        int level = 1;
        GameValues.Register("test.level", () => level);
        Assert.AreEqual(1, GameValues.Get(new[] { "test.level" })["test.level"]);
        level = 2;
        Assert.AreEqual(2, GameValues.Get(new[] { "test.level" })["test.level"]);
    }

    [Test]
    public void UnknownKeys_AreListed()
    {
        var e = Assert.Throws<QaplayException>(() => GameValues.Get(new[] { "test.level", "test.nope", "test.nah" }));
        Assert.AreEqual("unknown_key", e.code);
        CollectionAssert.AreEqual(new[] { "test.nope", "test.nah" }, e.keys);

        GameValues.Unregister("test.level");
        Assert.AreEqual("unknown_key", Assert.Throws<QaplayException>(() => GameValues.Get(new[] { "test.level" })).code);
    }

    // 프로토콜로 보낼 수 없는 값(객체 등)을 등록하면 읽을 때 알린다
    [Test]
    public void UnsupportedValueType_IsInternalError()
    {
        GameValues.Register("test.position", () => new int[2]);
        Assert.AreEqual("internal", Assert.Throws<QaplayException>(() => GameValues.Get(new[] { "test.position" })).code);
    }

    // 화면 글자("1,100")가 아니라 실제 값(1100)으로 확인한다
    [UnityTest]
    public IEnumerator Get_ReadsGoldFromSampleShop()
    {
        SampleShop.Build();
        yield return null;
        Assert.AreEqual(1200, GameValues.Get(new[] { "gold" })["gold"]);

        Vector2 point = UguiTap.PointOf(UguiDumper.Dump(), new UiSelector { name = "BuyButton" });
        yield return TouchInjector.Tap(point.x, point.y);
        Assert.AreEqual(1100, GameValues.Get(new[] { "gold" })["gold"]);
    }
}
