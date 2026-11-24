using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Qaplay;
using Qaplay.UGUI;
using TMPro;
using UnityEngine;
using UnityEngine.TestTools;

public class DumpTests
{
    // 샘플 화면을 dump해서, SampleShop.cs에 적힌 값과 비교한다.
    // 샘플은 정규화 좌표로 배치되어 있어 해상도가 달라도 기대값이 같다.
    [UnityTest]
    public IEnumerator Dump_SampleShop_MatchesWhatSampleBuilds()
    {
        SampleShop.Build();
        yield return null;

        UiDump dump = UguiDumper.Dump();
        var nodes = new Dictionary<string, UiNode>();
        int total = 0;
        foreach (UiNode root in dump.roots) total += Flatten(root, null, nodes);

        Expect(nodes, "Canvas", "Node", null, 0f, 0f, 1f, 1f, true, false);
        Expect(nodes, "Canvas/GoldLabel", "Text", "1,200", 0.7f, 0.02f, 0.25f, 0.03f, true, false);
        Expect(nodes, "Canvas/BuyButton", "Button", "구매", 0.35f, 0.82f, 0.3f, 0.05f, true, true);
        Expect(nodes, "Canvas/BuyButton/Text", "Text", "구매", 0.35f, 0.82f, 0.3f, 0.05f, true, false);
        Expect(nodes, "Canvas/SoldOutButton", "Button", "품절", 0.35f, 0.9f, 0.3f, 0.05f, true, false);
        Expect(nodes, "Canvas/SoldOutButton/Text", "Text", "품절", 0.35f, 0.9f, 0.3f, 0.05f, true, false);
        Expect(nodes, "Canvas/FadedPanel", "Image", null, 0.1f, 0.3f, 0.8f, 0.2f, false, false);
        Expect(nodes, "Canvas/FadedPanel/FadedText", "Text", "안 보임", 0.1f, 0.3f, 0.8f, 0.2f, false, false);
        Expect(nodes, "Canvas/OffscreenBadge", "Text", "화면 밖", 1.2f, 0.5f, 0.2f, 0.05f, false, false);
        // 가려진 버튼도 dump에서는 visible이다. 가려졌는지는 누를 때 검사한다
        Expect(nodes, "Canvas/CoveredButton", "Button", "가려짐", 0.05f, 0.6f, 0.2f, 0.05f, true, true);
        Expect(nodes, "Canvas/CoveredButton/Text", "Text", "가려짐", 0.05f, 0.6f, 0.2f, 0.05f, true, false);
        Expect(nodes, "Canvas/Cover", "Image", null, 0.03f, 0.58f, 0.24f, 0.09f, true, false);

        // 고유 ID는 QaplayId를 붙인 요소에만 나온다
        Assert.AreEqual("shop.buy", nodes["Canvas/BuyButton"].id);
        Assert.IsNull(nodes["Canvas/BuyButton/Text"].id);
        Assert.IsNull(nodes["Canvas/GoldLabel"].id);

        // TextMesh Pro 글자도 구형 Text와 같은 종류와 글자로 나온다
        Expect(nodes, "Canvas/Title", "Text", "SHOP", 0.3f, 0.1f, 0.4f, 0.05f, true, false);
        Expect(nodes, "Canvas/RefreshButton", "Button", "REFRESH", 0.7f, 0.82f, 0.25f, 0.05f, true, true);
        Expect(nodes, "Canvas/RefreshButton/Text", "Text", "REFRESH", 0.7f, 0.82f, 0.25f, 0.05f, true, false);

        // 스크롤 목록: 내용(Content)은 영역의 두 배 높이이고, 아이템은 이름이 모두 같다
        Expect(nodes, "Canvas/SelectedLabel", "Text", "-", 0.05f, 0.52f, 0.25f, 0.04f, true, false);
        Expect(nodes, "Canvas/ItemList", "ScrollView", null, 0.35f, 0.52f, 0.55f, 0.22f, true, false);
        Expect(nodes, "Canvas/ItemList/Viewport", "Node", null, 0.35f, 0.52f, 0.55f, 0.22f, true, false);
        Expect(nodes, "Canvas/ItemList/Viewport/Content", "Node", null, 0.35f, 0.52f, 0.55f, 0.44f, true, false);
        Expect(nodes, "Canvas/ItemList/Viewport/Content/Item", "Button", "검", 0.35f, 0.52f, 0.55f, 0.22f / 3f, true, true);
        Expect(nodes, "Canvas/InfoButton", "Button", "안내", 0.05f, 0.9f, 0.25f, 0.05f, true, true);
        Expect(nodes, "Canvas/InfoButton/Text", "Text", "안내", 0.05f, 0.9f, 0.25f, 0.05f, true, false);

        // 위 22개와 나머지 목록 아이템 5개(각각 버튼과 글자), 첫 아이템의 글자까지 33개. 꺼진 InfoPopup은 나오면 안 된다
        Assert.AreEqual(33, total, "요소: " + string.Join(", ", nodes.Keys));
        Assert.IsFalse(nodes.ContainsKey("Canvas/InfoPopup"));
    }

    // TextMesh Pro의 입력창과 드롭다운도 구형과 같은 종류로 나온다
    [UnityTest]
    public IEnumerator Dump_RecognizesTextMeshProControls()
    {
        var root = new GameObject("TmpCanvas", typeof(RectTransform), typeof(Canvas));
        var input = new GameObject("NameInput", typeof(RectTransform), typeof(TMP_InputField));
        input.transform.SetParent(root.transform, false);
        input.GetComponent<TMP_InputField>().text = "abc";
        new GameObject("SortDropdown", typeof(RectTransform), typeof(TMP_Dropdown)).transform.SetParent(root.transform, false);
        yield return null;

        UiDump dump = UguiDumper.Dump();
        UiNode inputNode = UiFinder.FindOne(dump, new UiSelector { name = "NameInput" }).node;
        Assert.AreEqual("InputField", inputNode.type);
        Assert.AreEqual("abc", inputNode.text);
        Assert.IsTrue(inputNode.interactable);
        Assert.AreEqual("Dropdown", UiFinder.FindOne(dump, new UiSelector { name = "SortDropdown" }).node.type);

        Object.Destroy(root);
    }

    [Test]
    public void ToJson_EscapesStringsAndRoundsNumbers()
    {
        var dump = new UiDump { screenWidth = 1080, screenHeight = 2400 };
        var node = new UiNode { name = "a\"b\\c", type = "Text", id = "a.b", text = "줄1\n줄2", x = 0.123456f, w = 1f, visible = true };
        node.children.Add(new UiNode { name = "child", type = "Node" });
        dump.roots.Add(node);

        Assert.AreEqual(
            "{\"screen\":{\"width\":1080,\"height\":2400},\"roots\":[" +
            "{\"name\":\"a\\\"b\\\\c\",\"type\":\"Text\",\"id\":\"a.b\",\"text\":\"줄1\\n줄2\"," +
            "\"rect\":{\"x\":0.1235,\"y\":0,\"w\":1,\"h\":0},\"visible\":true,\"interactable\":false,\"children\":[" +
            "{\"name\":\"child\",\"type\":\"Node\"," +
            "\"rect\":{\"x\":0,\"y\":0,\"w\":0,\"h\":0},\"visible\":false,\"interactable\":false,\"children\":[]}" +
            "]}]}",
            dump.ToJson());
    }

    // 경로별로 요소를 담고 전체 요소 수를 돌려준다. 경로가 같은 요소(목록 아이템)는 첫 번째만 담는다
    static int Flatten(UiNode node, string parentPath, Dictionary<string, UiNode> into)
    {
        string path = parentPath == null ? node.name : parentPath + "/" + node.name;
        if (!into.ContainsKey(path)) into.Add(path, node);
        int count = 1;
        foreach (UiNode child in node.children) count += Flatten(child, path, into);
        return count;
    }

    static void Expect(Dictionary<string, UiNode> nodes, string path, string type, string text,
        float x, float y, float w, float h, bool visible, bool interactable)
    {
        Assert.IsTrue(nodes.TryGetValue(path, out UiNode n), path + " 없음");
        Assert.AreEqual(type, n.type, path + " type");
        Assert.AreEqual(text, n.text, path + " text");
        Assert.AreEqual(x, n.x, 0.0005f, path + " x");
        Assert.AreEqual(y, n.y, 0.0005f, path + " y");
        Assert.AreEqual(w, n.w, 0.0005f, path + " w");
        Assert.AreEqual(h, n.h, 0.0005f, path + " h");
        Assert.AreEqual(visible, n.visible, path + " visible");
        Assert.AreEqual(interactable, n.interactable, path + " interactable");
    }
}
