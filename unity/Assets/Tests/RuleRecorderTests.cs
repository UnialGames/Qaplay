using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Qaplay;
using Qaplay.Agent;
using Qaplay.Injection;
using Qaplay.UGUI;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.TestTools;
using TouchPhase = UnityEngine.InputSystem.TouchPhase;

// 입력 기록: 누른 요소를 셀렉터로 바꾸는 규칙(docs/rules.md 7절)과, 실제 입력을 듣고 규칙 파일로 만드는 전체 경로
public class RuleRecorderTests
{
    // ---- 요소 → 셀렉터 (화면 없이) ----

    static UiNode Node(string name, params UiNode[] children)
    {
        var node = new UiNode { name = name, type = "Image" };
        node.children.AddRange(children);
        return node;
    }

    static UiDump Dump(params UiNode[] roots)
    {
        var dump = new UiDump { screenWidth = 100, screenHeight = 100 };
        dump.roots.AddRange(roots);
        return dump;
    }

    [Test]
    public void SelectorFor_AddsScopeThenIndexWhenNamesRepeat()
    {
        UiNode second = Node("Slot"), other = Node("Slot");
        UiDump dump = Dump(Node("Canvas",
            Node("List", Node("Slot"), second, Node("Slot")),
            Node("Other", other)));

        // 범위 안에서도 여럿이면 순서까지 적는다
        Assert.AreEqual("- tapOn: { name: Slot, within: { name: List }, index: 1 }",
            RuleRecorder.TapLine(RuleRecorder.SelectorFor(dump, second)));
        // 범위만으로 하나로 정해지면 순서는 적지 않는다
        Assert.AreEqual("- tapOn: { name: Slot, within: { name: Other } }",
            RuleRecorder.TapLine(RuleRecorder.SelectorFor(dump, other)));
    }

    // 범위로 쓸 조상이 하나도 없으면 화면 전체에서 센다
    [Test]
    public void SelectorFor_WithoutAUniqueAncestor_CountsOnTheWholeScreen()
    {
        UiNode target = Node("Slot");
        UiDump dump = Dump(Node("Canvas", Node("Slot")), Node("Canvas", target));

        Assert.AreEqual("- tapOn: { name: Slot, index: 1 }", RuleRecorder.TapLine(RuleRecorder.SelectorFor(dump, target)));
    }

    // 어떤 이름과 글자든, 적은 줄을 다시 읽으면 같은 셀렉터가 나온다
    [Test]
    public void TapLine_RoundTripsThroughTheReader()
    {
        string[] texts =
        {
            "구매", "Buy now", "shop.buy", "Button (1)", "100", "1,100", "-5", "true", "null", "", " 앞 공백", "뒤 공백 ",
            "가격: 100", "아이템 #3", "\"따옴표\"", "it's", "{x}", "[a]", "- dash", "줄\n바꿈", "탭\t", "역슬래시 \\", "e5", "*별",
        };
        foreach (string text in texts)
        {
            var selector = new UiSelector { name = text, text = text, within = new UiSelector { id = text }, index = 2 };
            string line = RuleRecorder.TapLine(selector);
            var read = (Dictionary<string, object>)RuleReader.Read(line).steps[0].args["selector"];

            Assert.AreEqual(text, read["name"], line);
            Assert.AreEqual(text, read["text"], line);
            Assert.AreEqual(text, ((Dictionary<string, object>)read["within"])["id"], line);
            Assert.AreEqual(2, read["index"], line);
        }
        Assert.AreEqual("- tapOn: { text: 구매 }", RuleRecorder.TapLine(new UiSelector { text = "구매" }));
        Assert.AreEqual("- tapOn: { text: \"1,100\" }", RuleRecorder.TapLine(new UiSelector { text = "1,100" }));
    }

    // ---- 샘플 화면 ----

    static UiNode Find(UiSelector selector) => UiFinder.FindOne(UguiDumper.Dump(), selector).node;

    static string LineFor(UiSelector selector)
    {
        UiDump dump = UguiDumper.Dump();
        return RuleRecorder.TapLine(RuleRecorder.SelectorFor(dump, UiFinder.FindOne(dump, selector).node));
    }

    [UnityTest]
    public IEnumerator SelectorFor_OnSampleShop_FollowsTheDocumentedOrder()
    {
        SampleShop.Build();
        yield return null;

        // 고유 ID가 있으면 ID
        Assert.AreEqual("- tapOn: { id: shop.buy }", LineFor(new UiSelector { name = "BuyButton" }));
        // 없으면 이름
        Assert.AreEqual("- tapOn: { name: InfoButton }", LineFor(new UiSelector { name = "InfoButton" }));
        // 이름이 여럿이면(Item 6개) 글자
        Assert.AreEqual("- tapOn: { text: 방패 }", LineFor(new UiSelector { name = "Item", index = 1 }));
        // 이름도 글자도 하나로 정해지지 않으면 범위. 범위는 가장 가까운 조상의 고유 ID나 이름
        Assert.AreEqual("- tapOn: { name: Text, within: { id: shop.buy } }",
            LineFor(new UiSelector { name = "Text", within = new UiSelector { name = "BuyButton" } }));
    }

    [TearDown]
    public void StopRecording()
    {
        UguiRecorder.Stop();
        if (m_Mouse != null && m_Mouse.added) InputSystem.RemoveDevice(m_Mouse);
        m_Mouse = null;
    }

    Mouse m_Mouse;

    static Vector2 PixelOf(UiSelector selector)
    {
        Vector2 point = UguiTap.PointOf(UguiDumper.Dump(), selector);
        return new Vector2(point.x * Screen.width, (1f - point.y) * Screen.height);
    }

    // 사람의 터치를 흉내 낸다: 그 요소의 가운데를 가상 터치로 누른다
    static IEnumerator Tap(UiSelector selector)
    {
        // 방금 열린 팝업은 한 번 그려진 뒤에야 터치를 받는다. 사람도 화면에 보인 뒤에 누른다
        yield return null;
        Vector2 point = UguiTap.PointOf(UguiDumper.Dump(), selector);
        yield return TouchInjector.Tap(point.x, point.y);
    }

    static readonly string[] SampleSession =
    {
        "- tapOn: { id: shop.buy }",
        "- tapOn: { name: InfoButton }",
        "- tapOn: { name: CloseButton }",
        "- tapOn: { text: 방패 }",
    };

    // 구매 → 안내 열기 → 닫기 → 목록에서 방패를 누르는 동안 기록한다
    static IEnumerator RecordSampleSession()
    {
        SampleShop.Build();
        yield return null;
        UguiRecorder.Start();
        yield return Tap(new UiSelector { name = "BuyButton" });
        yield return Tap(new UiSelector { name = "InfoButton" });
        yield return Tap(new UiSelector { name = "CloseButton" });
        yield return Tap(new UiSelector { name = "Item", index = 1 });
        UguiRecorder.Stop();
        CollectionAssert.AreEqual(SampleSession, UguiRecorder.Lines);
    }

    // 기록한 규칙을 처음 상태의 화면에서 재생하고, 같은 결과가 났는지 확인한다
    static IEnumerator ReplayOnFreshScreen(string yaml)
    {
        SampleShop.Build();
        yield return null;
        RuleResult result = null;
        yield return RuleRunner.Run(RuleReader.Read(yaml), AgentSession.Local().Call, r => result = r);

        Assert.IsTrue(result.passed, result.ToString());
        Assert.AreEqual(1100, GameValueReader.Get(new[] { "gold" })["gold"]);
        Assert.AreEqual("방패", Find(new UiSelector { name = "SelectedLabel" }).text);
        Assert.AreEqual(0, UiFinder.Find(UguiDumper.Dump(), new UiSelector { name = "InfoPopup" }).Count);
    }

    [UnityTest]
    public IEnumerator Recorder_RecordsTapsAndTheRuleReplays()
    {
        yield return RecordSampleSession();

        // 기록을 멈춘 뒤의 터치는 기록되지 않는다
        yield return Tap(new UiSelector { name = "BuyButton" });
        Assert.AreEqual(SampleSession.Length, UguiRecorder.Lines.Count);

        yield return ReplayOnFreshScreen(UguiRecorder.ToYaml());
    }

    // 끝났다는 기준: 기록한 규칙을 다른 해상도에서 재생해 통과한다
    [UnityTest]
    public IEnumerator RecordedRule_ReplaysAtAnotherResolution()
    {
#if UNITY_EDITOR
        // 사람이 쓰는 에디터의 Game 뷰 크기를 건드리지 않도록 배치 모드에서만 돌린다
        if (!Application.isBatchMode) Assert.Ignore("changes the Game view size; runs in batch mode only");

        SampleShop.Build();
        yield return null;
        int width = Screen.width, height = Screen.height;
        Vector2 recordedAt = PixelOf(new UiSelector { name = "BuyButton" });
        yield return RecordSampleSession();
        string yaml = UguiRecorder.ToYaml();

        // 가로 화면에서 기록했으면 세로로, 세로였으면 가로로 바꾼다
        uint newWidth = width >= height ? 540u : 1280u, newHeight = width >= height ? 960u : 720u;
        UnityEditor.PlayModeWindow.SetCustomRenderingResolution(newWidth, newHeight, "qaplay test");
        try
        {
            yield return null;
            yield return null;
            if (Screen.width == width && Screen.height == height)
                Assert.Inconclusive("the Game view size did not change");

            SampleShop.Build();
            yield return null;
            Vector2 replayedAt = PixelOf(new UiSelector { name = "BuyButton" });
            Debug.Log("[dbg] recorded at " + width + "x" + height + " (BuyButton at " + recordedAt + "), replayed at "
                      + Screen.width + "x" + Screen.height + " (BuyButton at " + replayedAt + ")");
            Assert.AreNotEqual(recordedAt, replayedAt, "버튼의 픽셀 위치가 달라져야 좌표가 아닌 요소 기준임을 확인할 수 있다");

            yield return ReplayOnFreshScreen(yaml);
        }
        finally
        {
            UnityEditor.PlayModeWindow.SetCustomRenderingResolution((uint)width, (uint)height, "qaplay test");
        }
#else
        yield break;
#endif
    }

    // 마우스로 눌러도 같은 길로 기록된다(에디터의 Game 뷰에서 사람이 쓰는 방법)
    [UnityTest]
    public IEnumerator Recorder_RecordsMouseClicks()
    {
        SampleShop.Build();
        yield return null;
        // 주입기를 한 번 쓰면 가상 장치의 입력이 창 포커스와 무관하게 게임으로 간다
        yield return TouchInjector.Tap(0.5f, 0.2f);
        m_Mouse = InputSystem.AddDevice<Mouse>();
        yield return null;

        UguiRecorder.Start();
        Vector2 pixel = PixelOf(new UiSelector { name = "InfoButton" });
        InputSystem.QueueStateEvent(m_Mouse, new MouseState { position = pixel });
        yield return null;
        InputSystem.QueueStateEvent(m_Mouse, new MouseState { position = pixel }.WithButton(MouseButton.Left));
        yield return null;
        InputSystem.QueueStateEvent(m_Mouse, new MouseState { position = pixel });
        yield return null;
        yield return null;

        CollectionAssert.AreEqual(new[] { "- tapOn: { name: InfoButton }" }, UguiRecorder.Lines);
        // 클릭이 실제로 버튼에 닿아 팝업이 열렸다
        Assert.AreEqual(1, UiFinder.Find(UguiDumper.Dump(), new UiSelector { name = "InfoPopup" }).Count);
    }

    [UnityTest]
    public IEnumerator Recorder_SkipsDragsAndNotesTapsOnNothing()
    {
        SampleShop.Build();
        yield return null;
        yield return TouchInjector.Tap(0.5f, 0.2f); // 가상 터치스크린을 만든다
        Touchscreen screen = Touchscreen.current;
        UguiRecorder.Start();

        // 끌기: 목록의 아이템에서 눌러 멀리 가서 뗀다. 탭이 아니므로 기록하지 않는다
        Vector2 from = PixelOf(new UiSelector { name = "Item", index = 0 });
        Vector2 to = from + new Vector2(0f, Screen.height * 0.2f);
        const int touchId = 900001;
        InputSystem.QueueStateEvent(screen, new TouchState { touchId = touchId, phase = TouchPhase.Began, position = from, pressure = 1f });
        yield return null;
        InputSystem.QueueStateEvent(screen, new TouchState { touchId = touchId, phase = TouchPhase.Moved, position = to, pressure = 1f });
        yield return null;
        InputSystem.QueueStateEvent(screen, new TouchState { touchId = touchId, phase = TouchPhase.Ended, position = to });
        yield return null;
        yield return null;
        Assert.AreEqual(0, UguiRecorder.Lines.Count, string.Join(" / ", UguiRecorder.Lines));

        // 아무 요소도 받지 않는 자리를 누르면 명령 대신 주석으로 남긴다
        yield return TouchInjector.Tap(0.5f, 0.2f);
        CollectionAssert.AreEqual(new[] { "# not recorded: no element takes a tap at (0.5, 0.2)" }, UguiRecorder.Lines);
        // 주석이 섞여도 규칙 파일로 읽힌다(명령이 없다는 오류만 난다)
        StringAssert.Contains("no commands", Assert.Throws<System.FormatException>(() => RuleReader.Read(UguiRecorder.ToYaml())).Message);
    }
}
