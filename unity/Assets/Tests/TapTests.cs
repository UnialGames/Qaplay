using System.Collections;
using NUnit.Framework;
using Qaplay;
using Qaplay.Injection;
using Qaplay.UGUI;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.TestTools;

public class TapTests
{
    static string Gold() =>
        UiFinder.FindOne(UguiDumper.Dump(), new UiSelector { name = "GoldLabel" }).node.text;

    // 가상 터치가 실제 입력 경로(Input System → UI 입력 모듈 → Button.onClick)를 거쳐 게임 상태를 바꾸는지 확인한다
    [UnityTest]
    public IEnumerator Tap_BuyButton_SpendsGold()
    {
        SampleShop.Build();
        yield return null;
        Assert.AreEqual("1,200", Gold());

        // 글자만으로 찾는다. 버튼과 그 안의 Text가 함께 걸리지만 바깥 요소인 버튼으로 정해진다
        Vector2 point = UguiTap.PointOf(UguiDumper.Dump(), new UiSelector { text = "구매" });
        Assert.AreEqual(0.5f, point.x, 0.0005f);
        Assert.AreEqual(0.845f, point.y, 0.0005f);

        yield return TouchInjector.Tap(point.x, point.y);
        Assert.AreEqual("1,100", Gold());

        // 연달아 눌러도 한 번에 한 번씩만 처리된다
        yield return TouchInjector.Tap(point.x, point.y);
        Assert.AreEqual("1,000", Gold());
    }

    // 누를 수 없는 요소는 터치를 넣기 전에 이유와 함께 거절한다
    [UnityTest]
    public IEnumerator Tap_UntappableElements_AreRefusedWithReason()
    {
        SampleShop.Build();
        yield return null;
        UiDump dump = UguiDumper.Dump();

        Assert.AreEqual("not_interactable", ReasonOf(dump, "SoldOutButton"));
        Assert.AreEqual("off_screen", ReasonOf(dump, "OffscreenBadge"));
        Assert.AreEqual("not_visible", ReasonOf(dump, "FadedPanel"));
        Assert.AreEqual("occluded", ReasonOf(dump, "CoveredButton"));
    }

    // 에디터에서 테스트를 돌리는 동안 사람이 마우스를 움직여도 탭은 전달되어야 한다.
    // UI 입력 모듈은 마우스와 터치가 같은 프레임에 들어오면 한쪽 포인터를 버리기 때문에 따로 확인한다.
    [UnityTest]
    public IEnumerator Tap_WorksWhileMouseIsMoving()
    {
        SampleShop.Build();
        m_Mouse = InputSystem.AddDevice<Mouse>();
        m_Noise = new GameObject("MouseNoise").AddComponent<MouseNoise>();
        m_Noise.mouse = m_Mouse;
        yield return null;
        yield return null;

        Vector2 point = UguiTap.PointOf(UguiDumper.Dump(), new UiSelector { text = "구매" });
        yield return TouchInjector.Tap(point.x, point.y);
        Assert.AreEqual("1,100", Gold());

        // 탭이 끝난 뒤에도 마우스는 계속 움직인다. 그 움직임 때문에 같은 버튼이 한 번 더 눌리면 안 된다
        for (int frame = 0; frame < 10; frame++) yield return null;
        Assert.AreEqual("1,100", Gold(), "탭 한 번에 클릭은 한 번");
    }

    // 사람이 마우스 버튼을 누르고 있는 중에 탭이 들어와도, 탭한 버튼이 한 번 더 눌리면 안 된다.
    // 에디터에서 테스트가 도는 동안 사람이 다른 창을 누르면 생기는 상황이다(가끔 나던 중복 클릭의 원인).
    [UnityTest]
    public IEnumerator Tap_IsNotRepeatedByAMouseButtonPressedAroundIt()
    {
        var away = new Vector2(3f, 3f); // 화면 왼쪽 아래 구석. 아무 요소도 없다
        m_Mouse = InputSystem.AddDevice<Mouse>();

        // 1. 누르고 있는 중에 탭이 들어오고, 탭이 끝난 뒤에 뗀다
        SampleShop.Build();
        InputSystem.QueueStateEvent(m_Mouse, new MouseState { position = away });
        yield return null;
        yield return null;
        Vector2 point = UguiTap.PointOf(UguiDumper.Dump(), new UiSelector { text = "구매" });
        InputSystem.QueueStateEvent(m_Mouse, new MouseState { position = away }.WithButton(MouseButton.Left));
        yield return null;
        yield return TouchInjector.Tap(point.x, point.y);
        // 장치를 껐다 켜면 Input System이 장치의 현재 상태를 다시 받아 온다. 버튼이 아직 눌려 있으면 눌림이 새로 들어온다
        InputSystem.QueueStateEvent(m_Mouse, new MouseState { position = away }.WithButton(MouseButton.Left));
        yield return null;
        yield return null;
        InputSystem.QueueStateEvent(m_Mouse, new MouseState { position = away });
        for (int frame = 0; frame < 5; frame++) yield return null;
        Assert.AreEqual("1,100", Gold(), "누르고 있던 마우스 버튼이 탭한 버튼을 다시 눌렀다");

        // 2. 탭이 끝난 바로 다음 프레임에 마우스를 움직이지 않고 새로 누른다
        SampleShop.Build();
        yield return null;
        yield return TouchInjector.Tap(point.x, point.y);
        InputSystem.QueueStateEvent(m_Mouse, new MouseState { position = away }.WithButton(MouseButton.Left));
        yield return null;
        yield return null;
        InputSystem.QueueStateEvent(m_Mouse, new MouseState { position = away });
        for (int frame = 0; frame < 5; frame++) yield return null;
        Assert.AreEqual("1,100", Gold(), "탭 직후의 마우스 클릭이 탭한 버튼을 다시 눌렀다");
    }

    // 탭이 끝난 뒤 사람이 마우스를 움직이지 않고 누르면 커서 아래의 요소가 눌려야 한다.
    // UI 입력 모듈은 터치가 들어오면 마우스의 포인터 기록을 지운다. 그대로 두면 다음 클릭이 화면 왼쪽 아래 구석 (0, 0)에서
    // 처리되어, 구석에 있는 요소가 대신 눌린다
    [UnityTest]
    public IEnumerator MouseClickAfterTap_WithoutMovingTheMouse_LandsUnderTheCursor()
    {
        SampleShop.Build();
        yield return null;
        yield return TouchInjector.Tap(0.5f, 0.2f); // 가상 터치스크린을 만든다. 그 뒤에 추가한 마우스라야 포커스 없이도 게임에 닿는다
        m_Mouse = InputSystem.AddDevice<Mouse>();
        // 에디터처럼 한 번도 쓰지 않은 펜 장치가 함께 있다. 펜의 위치는 (0, 0)이다
        m_Pen = InputSystem.AddDevice<Pen>();

        // 왼쪽 아래 구석에 버튼을 하나 둔다
        int cornerClicks = 0;
        var corner = new GameObject("CornerButton", typeof(RectTransform), typeof(UnityEngine.UI.Image), typeof(UnityEngine.UI.Button));
        corner.transform.SetParent(GameObject.Find("Canvas").transform, false);
        var rect = (RectTransform)corner.transform;
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = new Vector2(0.04f, 0.04f);
        rect.offsetMin = rect.offsetMax = Vector2.zero;
        corner.GetComponent<UnityEngine.UI.Button>().onClick.AddListener(() => cornerClicks++);

        // 마우스를 구매 버튼 위에 올려 두고, 그 자리를 탭한다
        var onBuy = new Vector2(0.5f * Screen.width, (1f - 0.845f) * Screen.height);
        InputSystem.QueueStateEvent(m_Mouse, new MouseState { position = new Vector2(0.5f * Screen.width, 0.95f * Screen.height) });
        yield return null;
        InputSystem.QueueStateEvent(m_Mouse, new MouseState { position = onBuy });
        yield return null;
        yield return TouchInjector.Tap(0.5f, 0.845f);
        Assert.AreEqual("1,100", Gold());

        // 마우스를 움직이지 않고 두 번 누른다
        for (int click = 0; click < 2; click++)
        {
            InputSystem.QueueStateEvent(m_Mouse, new MouseState { position = onBuy }.WithButton(MouseButton.Left));
            yield return null;
            yield return null;
            InputSystem.QueueStateEvent(m_Mouse, new MouseState { position = onBuy });
            for (int frame = 0; frame < 3; frame++) yield return null;
        }
        Assert.AreEqual(0, cornerClicks, "사람의 클릭이 구석의 요소에 들어갔다");
        Assert.AreEqual("900", Gold(), "사람의 클릭 두 번이 커서 아래의 구매 버튼에 들어가야 한다");
    }

    Mouse m_Mouse;
    Pen m_Pen;
    MouseNoise m_Noise;

    [TearDown]
    public void RemoveMouseNoise()
    {
        if (m_Noise != null) Object.Destroy(m_Noise.gameObject);
        if (m_Mouse != null && m_Mouse.added) InputSystem.RemoveDevice(m_Mouse);
        if (m_Pen != null && m_Pen.added) InputSystem.RemoveDevice(m_Pen);
        m_Pen = null;
        m_Noise = null;
        m_Mouse = null;
    }

    static string ReasonOf(UiDump dump, string name)
    {
        var e = Assert.Throws<QaplayException>(() => UguiTap.PointOf(dump, new UiSelector { name = name }));
        Assert.AreEqual("not_tappable", e.code, name);
        return e.reason;
    }
}

// 매 프레임 마우스 위치를 바꾼다. 사람이 마우스를 움직이는 상황을 흉내 낸다.
public class MouseNoise : MonoBehaviour
{
    public Mouse mouse;
    int m_Frame;

    void Update()
    {
        InputSystem.QueueStateEvent(mouse, new MouseState { position = new Vector2(100 + m_Frame++ % 50, 100) });
    }
}
