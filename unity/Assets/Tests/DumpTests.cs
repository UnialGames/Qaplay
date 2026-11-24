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
        foreach (UiNode root in dump.roots) Flatten(root, null, nodes);

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

        // TextMesh Pro 글자도 구형 Text와 같은 종류와 글자로 나온다
        Expect(nodes, "Canvas/Title", "Text", "SHOP", 0.3f, 0.1f, 0.4f, 0.05f, true, false);
        Expect(nodes, "Canvas/RefreshButton", "Button", "REFRESH", 0.7f, 0.82f, 0.25f, 0.05f, true, true);
        Expect(nodes, "Canvas/RefreshButton/Text", "Text", "REFRESH", 0.7f, 0.82f, 0.25f, 0.05f, true, false);

        // 꺼진 HiddenPopup을 포함해 위 15개 말고는 나오면 안 된다
        Assert.AreEqual(15, nodes.Count, "요소: " + string.Join(", ", nodes.Keys));
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
        var node = new UiNode { name = "a\"b\\c", type = "Text", text = "줄1\n줄2", x = 0.123456f, w = 1f, visible = true };
        node.children.Add(new UiNode { name = "child", type = "Node" });
        dump.roots.Add(node);

        Assert.AreEqual(
            "{\"screen\":{\"width\":1080,\"height\":2400},\"roots\":[" +
            "{\"name\":\"a\\\"b\\\\c\",\"type\":\"Text\",\"text\":\"줄1\\n줄2\"," +
            "\"rect\":{\"x\":0.1235,\"y\":0,\"w\":1,\"h\":0},\"visible\":true,\"interactable\":false,\"children\":[" +
            "{\"name\":\"child\",\"type\":\"Node\"," +
            "\"rect\":{\"x\":0,\"y\":0,\"w\":0,\"h\":0},\"visible\":false,\"interactable\":false,\"children\":[]}" +
            "]}]}",
            dump.ToJson());
    }

    static void Flatten(UiNode node, string parentPath, Dictionary<string, UiNode> into)
    {
        string path = parentPath == null ? node.name : parentPath + "/" + node.name;
        into.Add(path, node);
        foreach (UiNode child in node.children) Flatten(child, path, into);
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
