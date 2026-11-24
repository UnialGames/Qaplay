using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Qaplay;
using Qaplay.UGUI;
using UnityEngine.TestTools;

public class FindTests
{
    // Canvas
    //   GoldLabel   Text   "1,200"
    //   BuyButton   Button "구매"
    //     Text      Text   "구매"
    //   ShopPopup   Node
    //     Item      Button "검"
    //     Item      Button "방패"
    //   Other       Node
    //     Item      Button "검"
    static UiDump Tree()
    {
        var dump = new UiDump();
        dump.roots.Add(
            N("Canvas", "Node", null,
                N("GoldLabel", "Text", "1,200"),
                N("BuyButton", "Button", "구매",
                    N("Text", "Text", "구매")),
                N("ShopPopup", "Node", null,
                    N("Item", "Button", "검"),
                    N("Item", "Button", "방패")),
                N("Other", "Node", null,
                    N("Item", "Button", "검"))));
        return dump;
    }

    static UiNode N(string name, string type, string text, params UiNode[] children)
    {
        var node = new UiNode { name = name, type = type, text = text };
        node.children.AddRange(children);
        return node;
    }

    static string[] Paths(UiSelector s) => UiFinder.Find(Tree(), s).Select(m => m.path).ToArray();

    static string CodeOf(TestDelegate action) => Assert.Throws<QaplayException>(action).code;

    [Test]
    public void Name_FindsElementWithPath()
    {
        CollectionAssert.AreEqual(new[] { "Canvas/GoldLabel" }, Paths(new UiSelector { name = "GoldLabel" }));
    }

    [Test]
    public void Type_ReturnsAllMatchesInDocumentOrder()
    {
        CollectionAssert.AreEqual(
            new[] { "Canvas/BuyButton", "Canvas/ShopPopup/Item", "Canvas/ShopPopup/Item", "Canvas/Other/Item" },
            Paths(new UiSelector { type = "Button" }));
    }

    [Test]
    public void MultipleConditions_AllMustMatch()
    {
        CollectionAssert.AreEqual(new[] { "Canvas/BuyButton" },
            Paths(new UiSelector { type = "Button", text = "구매" }));
        CollectionAssert.IsEmpty(Paths(new UiSelector { name = "GoldLabel", type = "Button" }));
    }

    // 고유 ID는 붙인 요소에만 있다. 붙이지 않은 요소는 id 조건에 걸리지 않는다
    [Test]
    public void Id_MatchesOnlyElementsWithThatId()
    {
        UiDump dump = Tree();
        dump.roots[0].children[1].id = "shop.buy"; // BuyButton

        Assert.AreEqual("Canvas/BuyButton", UiFinder.FindOne(dump, new UiSelector { id = "shop.buy" }).path);
        CollectionAssert.IsEmpty(UiFinder.Find(dump, new UiSelector { id = "shop.sell" }));
        // 다른 조건과 함께 쓰면 모두 만족해야 한다
        Assert.AreEqual(1, UiFinder.Find(dump, new UiSelector { id = "shop.buy", type = "Button" }).Count);
        CollectionAssert.IsEmpty(UiFinder.Find(dump, new UiSelector { id = "shop.buy", type = "Text" }));
    }

    // 버튼은 자손의 글자를 가지므로(명세 4절) 글자만으로 찾으면 버튼과 그 안의 Text가 함께 걸린다
    [Test]
    public void TextOnly_MatchesButtonAndItsLabel()
    {
        CollectionAssert.AreEqual(new[] { "Canvas/BuyButton", "Canvas/BuyButton/Text" },
            Paths(new UiSelector { text = "구매" }));
    }

    [Test]
    public void Within_LimitsToDescendantsOfScope()
    {
        CollectionAssert.AreEqual(new[] { "Canvas/ShopPopup/Item", "Canvas/ShopPopup/Item" },
            Paths(new UiSelector { name = "Item", within = new UiSelector { name = "ShopPopup" } }));
    }

    [Test]
    public void Within_ExcludesScopeItself()
    {
        CollectionAssert.IsEmpty(
            Paths(new UiSelector { name = "ShopPopup", within = new UiSelector { name = "ShopPopup" } }));
    }

    [Test]
    public void Index_PicksNthMatch()
    {
        List<UiMatch> third = UiFinder.Find(Tree(), new UiSelector { name = "Item", index = 2 });
        Assert.AreEqual(1, third.Count);
        Assert.AreEqual("Canvas/Other/Item", third[0].path);

        CollectionAssert.IsEmpty(Paths(new UiSelector { name = "Item", index = 3 }));
    }

    [Test]
    public void Index_AppliesAfterWithin()
    {
        UiMatch second = UiFinder.FindOne(Tree(),
            new UiSelector { name = "Item", within = new UiSelector { name = "ShopPopup" }, index = 1 });
        Assert.AreEqual("방패", second.node.text);
    }

    [Test]
    public void FindOne_NoMatch_IsNotFound()
    {
        Assert.AreEqual("not_found", CodeOf(() => UiFinder.FindOne(Tree(), new UiSelector { name = "Nope" })));
    }

    [Test]
    public void FindOne_ManyMatches_IsAmbiguous()
    {
        Assert.AreEqual("ambiguous", CodeOf(() => UiFinder.FindOne(Tree(), new UiSelector { name = "Item" })));
    }

    // 버튼과 그 안의 Text처럼 걸린 요소들이 한 줄로 이어진 조상과 자손이면 가장 바깥 요소로 정한다
    [Test]
    public void FindOne_NestedMatches_PicksOutermost()
    {
        Assert.AreEqual("Canvas/BuyButton", UiFinder.FindOne(Tree(), new UiSelector { text = "구매" }).path);
    }

    // Canvas 안에 ShopPopup과 Other가 나란히 있다. 한 줄로 이어지지 않으므로 고르지 않는다
    [Test]
    public void FindOne_NestedButBranching_IsAmbiguous()
    {
        Assert.AreEqual("ambiguous", CodeOf(() => UiFinder.FindOne(Tree(), new UiSelector { type = "Node" })));
    }

    [Test]
    public void Within_MustResolveToOneElement()
    {
        Assert.AreEqual("ambiguous", CodeOf(() => UiFinder.Find(Tree(),
            new UiSelector { type = "Text", within = new UiSelector { name = "Item" } })));
        Assert.AreEqual("not_found", CodeOf(() => UiFinder.Find(Tree(),
            new UiSelector { type = "Text", within = new UiSelector { name = "Nope" } })));
    }

    [Test]
    public void EmptySelectorOrNegativeIndex_IsBadRequest()
    {
        Assert.AreEqual("bad_request", CodeOf(() => UiFinder.Find(Tree(), new UiSelector())));
        Assert.AreEqual("bad_request", CodeOf(() => UiFinder.Find(Tree(), new UiSelector { name = "Item", index = -1 })));
    }

    // 실제 uGUI 화면(SampleShop)에서도 같은 방식으로 찾아지는지 확인한다
    [UnityTest]
    public IEnumerator Find_OnSampleShop()
    {
        SampleShop.Build();
        yield return null;
        UiDump dump = UguiDumper.Dump();

        UiMatch buy = UiFinder.FindOne(dump, new UiSelector { type = "Button", text = "구매" });
        Assert.AreEqual("Canvas/BuyButton", buy.path);
        Assert.IsTrue(buy.node.interactable);

        Assert.AreEqual(4, UiFinder.Find(dump, new UiSelector { type = "Button" }).Count);
        Assert.AreEqual("ambiguous", CodeOf(() => UiFinder.FindOne(dump, new UiSelector { type = "Button" })));
        Assert.AreEqual("not_found", CodeOf(() => UiFinder.FindOne(dump, new UiSelector { name = "HiddenPopup" })));
    }
}
