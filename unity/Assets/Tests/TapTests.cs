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

    Mouse m_Mouse;
    MouseNoise m_Noise;

    [TearDown]
    public void RemoveMouseNoise()
    {
        if (m_Noise != null) Object.Destroy(m_Noise.gameObject);
        if (m_Mouse != null && m_Mouse.added) InputSystem.RemoveDevice(m_Mouse);
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
