using System.Collections;
using System.Linq;
using NUnit.Framework;
using Qaplay;
using Qaplay.Injection;
using Qaplay.UGUI;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;

public class PopupAndListTests
{
    static readonly UiSelector ItemList = new UiSelector { name = "ItemList" };

    static UiSelector Item(string text) => new UiSelector { type = "Button", text = text, within = ItemList };

    static string TextOf(string name) => UiFinder.FindOne(UguiDumper.Dump(), new UiSelector { name = name }).node.text;

    // 목록 아이템 중 visible이 want인 것의 글자
    static string[] Items(bool want) =>
        UiFinder.Find(UguiDumper.Dump(), new UiSelector { name = "Item", within = ItemList })
            .Where(m => m.node.visible == want).Select(m => m.node.text).ToArray();

    // 누를 수 없으면 터치를 넣기 전에 QaplayException이 난다
    static IEnumerator Tap(UiSelector selector)
    {
        Vector2 point = UguiTap.PointOf(UguiDumper.Dump(), selector);
        return TouchInjector.Tap(point.x, point.y);
    }

    static string ReasonOf(UiSelector selector)
    {
        var e = Assert.Throws<QaplayException>(() => UguiTap.PointOf(UguiDumper.Dump(), selector));
        Assert.AreEqual("not_tappable", e.code);
        return e.reason;
    }

    // 스크롤 영역 밖으로 잘린 아이템은 계층에는 있지만 보이지 않는 것으로 나온다
    [UnityTest]
    public IEnumerator List_ItemsOutsideTheScrollAreaAreNotVisible()
    {
        SampleShop.Build();
        yield return null;
        CollectionAssert.AreEqual(new[] { "검", "방패", "활" }, Items(true));
        CollectionAssert.AreEqual(new[] { "지팡이", "갑옷", "투구" }, Items(false));

        Object.FindFirstObjectByType<ScrollRect>().verticalNormalizedPosition = 0f; // 맨 아래로
        yield return null;
        CollectionAssert.AreEqual(new[] { "지팡이", "갑옷", "투구" }, Items(true));
        CollectionAssert.AreEqual(new[] { "검", "방패", "활" }, Items(false));
    }

    [UnityTest]
    public IEnumerator List_TapNeedsTheItemScrolledIntoView()
    {
        SampleShop.Build();
        yield return null;
        Assert.AreEqual("-", TextOf("SelectedLabel"));

        yield return Tap(Item("방패"));
        Assert.AreEqual("방패", TextOf("SelectedLabel"));
        Assert.AreEqual("not_visible", ReasonOf(Item("갑옷")));

        Object.FindFirstObjectByType<ScrollRect>().verticalNormalizedPosition = 0f;
        yield return null;
        yield return Tap(Item("갑옷"));
        Assert.AreEqual("갑옷", TextOf("SelectedLabel"));
        Assert.AreEqual("not_visible", ReasonOf(Item("방패")));
    }

    // 일부만 보이는 아이템: 보이기는 하지만 중심이 잘려 있으면 터치가 닿지 않으므로 거절한다
    [UnityTest]
    public IEnumerator List_ItemWithClippedCenter_IsRefused()
    {
        SampleShop.Build();
        yield return null;

        // 첫 아이템이 높이의 60%만큼 영역 위로 밀려 나가게 한다 (아이템 높이는 화면의 0.22 / 3)
        RectTransform content = Object.FindFirstObjectByType<ScrollRect>().content;
        content.anchoredPosition += new Vector2(0f, 0.6f * (0.22f / 3f) * Screen.height);
        yield return null;

        Assert.Contains("검", Items(true));
        Assert.AreEqual("occluded", ReasonOf(Item("검")));
        // 온전히 보이는 다음 아이템은 그대로 눌린다
        yield return Tap(Item("방패"));
        Assert.AreEqual("방패", TextOf("SelectedLabel"));
    }

    // 팝업이 열려 있는 동안 뒤의 버튼은 가려지고, 닫으면 다시 눌린다
    [UnityTest]
    public IEnumerator Popup_CoversTheScreenUntilClosed()
    {
        SampleShop.Build();
        yield return null;
        var popup = new UiSelector { name = "InfoPopup" };
        var buy = new UiSelector { id = "shop.buy" };
        Assert.AreEqual(0, UiFinder.Find(UguiDumper.Dump(), popup).Count);

        yield return Tap(new UiSelector { name = "InfoButton" });
        yield return UguiWait.Until(popup, "visible", 2000);
        Assert.AreEqual("occluded", ReasonOf(buy));

        yield return Tap(new UiSelector { name = "CloseButton", within = popup });
        yield return UguiWait.Until(popup, "gone", 2000);
        yield return Tap(buy);
        Assert.AreEqual("1,100", TextOf("GoldLabel"));
    }
}
