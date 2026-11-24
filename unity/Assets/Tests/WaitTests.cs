using System.Collections;
using NUnit.Framework;
using Qaplay;
using Qaplay.Injection;
using Qaplay.UGUI;
using UnityEngine;
using UnityEngine.TestTools;

public class WaitTests
{
    // Canvas
    //   Shown    Text  (보임)
    //   Hidden   Text  (안 보임)
    //   Item     Button (보임)
    //   Item     Button (안 보임)
    static UiDump Tree()
    {
        var canvas = new UiNode { name = "Canvas", type = "Node", visible = true };
        canvas.children.Add(new UiNode { name = "Shown", type = "Text", visible = true });
        canvas.children.Add(new UiNode { name = "Hidden", type = "Text", visible = false });
        canvas.children.Add(new UiNode { name = "Item", type = "Button", visible = true });
        canvas.children.Add(new UiNode { name = "Item", type = "Button", visible = false });
        var dump = new UiDump();
        dump.roots.Add(canvas);
        return dump;
    }

    static bool Met(UiSelector s, string state, out int count) => UiWait.IsMet(Tree(), s, state, out count);

    [Test]
    public void Visible_NeedsAtLeastOneVisibleMatch()
    {
        Assert.IsTrue(Met(new UiSelector { name = "Shown" }, "visible", out _));
        Assert.IsTrue(Met(new UiSelector { name = "Item" }, "visible", out int items));
        Assert.AreEqual(2, items);

        // 있지만 보이지 않는 요소: 아직 visible이 아니고, 걸린 수는 1
        Assert.IsFalse(Met(new UiSelector { name = "Hidden" }, "visible", out int hidden));
        Assert.AreEqual(1, hidden);
        Assert.IsFalse(Met(new UiSelector { name = "Nothing" }, "visible", out int nothing));
        Assert.AreEqual(0, nothing);
    }

    [Test]
    public void Gone_NeedsNoVisibleMatch()
    {
        Assert.IsTrue(Met(new UiSelector { name = "Nothing" }, "gone", out _));
        Assert.IsTrue(Met(new UiSelector { name = "Hidden" }, "gone", out _));
        Assert.IsFalse(Met(new UiSelector { name = "Shown" }, "gone", out _));
        Assert.IsFalse(Met(new UiSelector { name = "Item" }, "gone", out _));
    }

    // within이 가리키는 요소가 아직 없으면 오류가 아니라 "걸린 요소 없음"이다. 팝업 안의 버튼을 기다릴 수 있어야 한다
    [Test]
    public void MissingScope_CountsAsNoMatch()
    {
        var inPopup = new UiSelector { name = "Item", within = new UiSelector { name = "Popup" } };
        Assert.IsFalse(Met(inPopup, "visible", out int count));
        Assert.AreEqual(0, count);
        Assert.IsTrue(Met(inPopup, "gone", out _));
    }

    [Test]
    public void BadArguments_AreRejected()
    {
        var shown = new UiSelector { name = "Shown" };
        Assert.AreEqual("bad_request", Assert.Throws<QaplayException>(() => Met(shown, "enabled", out _)).code);
        Assert.AreEqual("bad_request", Assert.Throws<QaplayException>(() => Met(new UiSelector(), "visible", out _)).code);
    }

    // 누르고 → 기다리고 → 확인: 구매하면 알림이 떴다가 0.5초 뒤 사라진다
    [UnityTest]
    public IEnumerator Wait_FollowsToastAppearingAndDisappearing()
    {
        SampleShop.Build();
        yield return null;
        var toast = new UiSelector { name = "Toast" };
        Assert.AreEqual(0, UiFinder.Find(UguiDumper.Dump(), toast).Count);

        Vector2 point = UguiTap.PointOf(UguiDumper.Dump(), new UiSelector { name = "BuyButton" });
        yield return TouchInjector.Tap(point.x, point.y);

        yield return UguiWait.Until(toast, "visible", 2000);
        Assert.AreEqual(1, UiFinder.Find(UguiDumper.Dump(), toast).Count);

        // 글자가 바뀌기를 기다릴 때는 셀렉터에 text를 넣는다
        yield return UguiWait.Until(new UiSelector { name = "GoldLabel", text = "1,100" }, "visible", 2000);

        float start = Time.realtimeSinceStartup;
        yield return UguiWait.Until(toast, "gone", 5000);
        Assert.AreEqual(0, UiFinder.Find(UguiDumper.Dump(), toast).Count);
        // 바로 끝난 것이 아니라 알림이 사라질 때까지 실제로 기다렸다
        Assert.Greater(Time.realtimeSinceStartup - start, 0.1f);
    }

    [UnityTest]
    public IEnumerator Wait_TimesOutWithMatchCount()
    {
        SampleShop.Build();
        yield return null;

        yield return Run(UguiWait.Until(new UiSelector { name = "NoSuchThing" }, "visible", 100));
        Assert.AreEqual("timeout", m_Error?.code);
        Assert.AreEqual(0, m_Error.count);

        // 사라지지 않는 요소: 마지막에 걸린 수가 함께 온다
        yield return Run(UguiWait.Until(new UiSelector { name = "BuyButton" }, "gone", 100));
        Assert.AreEqual("timeout", m_Error?.code);
        Assert.AreEqual(1, m_Error.count);
    }

    QaplayException m_Error;

    // 코루틴이 던진 QaplayException을 m_Error에 담는다. 던지지 않으면 null.
    IEnumerator Run(IEnumerator routine)
    {
        m_Error = null;
        while (true)
        {
            try
            {
                if (!routine.MoveNext()) yield break;
            }
            catch (QaplayException e)
            {
                m_Error = e;
                yield break;
            }
            yield return routine.Current;
        }
    }
}
