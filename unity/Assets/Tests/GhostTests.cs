using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Qaplay;
using Qaplay.Agent;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;

// ghosting 명령과 규칙의 assertGhosting: 이미지를 불러오는 동안 이전 그림이 보인 프레임.
public class GhostTests
{
    GameObject m_Root;
    readonly List<UnityEngine.Object> m_Pictures = new List<UnityEngine.Object>();
    Dictionary<string, object> m_Ghosting;
    RuleResult m_Result;

    [TearDown]
    public void Cleanup()
    {
        if (m_Root != null) UnityEngine.Object.DestroyImmediate(m_Root);
        foreach (UnityEngine.Object picture in m_Pictures) UnityEngine.Object.DestroyImmediate(picture);
        m_Pictures.Clear();
    }

    Texture2D Picture(string name)
    {
        var picture = new Texture2D(2, 2) { name = name };
        m_Pictures.Add(picture);
        return picture;
    }

    T Element<T>(string name) where T : Component
    {
        if (m_Root == null) m_Root = new GameObject("GhostCanvas", typeof(RectTransform), typeof(Canvas));
        m_Root.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(m_Root.transform, false);
        return go.AddComponent<T>();
    }

    RawImage Icon(string name, Texture previous)
    {
        var icon = Element<RawImage>(name);
        icon.texture = previous;
        return icon;
    }

    IEnumerator Take()
    {
        Dictionary<string, object> response = null;
        yield return AgentSession.Local().Call("ghosting", null, r => response = r);
        Assert.AreEqual(true, response["ok"], Json.Write(response));
        m_Ghosting = (Dictionary<string, object>)response["result"];
    }

    List<object> Images => (List<object>)m_Ghosting["images"];

    static IEnumerator Frames(int count)
    {
        for (int n = 0; n < count; n++) yield return null;
    }

    // 불러오기를 시작한 뒤 새 그림이 올 때까지 이전 그림을 그대로 둔 경우: 그 프레임들이 잔상이다
    [UnityTest]
    public IEnumerator Ghosting_CountsFramesThatShowThePreviousPicture()
    {
        RawImage icon = Icon("Icon", Picture("old-sword"));
        yield return Frames(2);
        yield return Take(); // 여기부터 센다

        ImageLoads.Started(icon);
        yield return Frames(6);
        icon.texture = Picture("new-shield");
        ImageLoads.Finished(icon);
        yield return Frames(3); // 끝난 뒤의 프레임은 세지 않는다
        yield return Take();

        Assert.AreEqual(1, Images.Count, Json.Write(m_Ghosting));
        var image = (Dictionary<string, object>)Images[0];
        Assert.AreEqual("GhostCanvas/Icon", image["path"]);
        Assert.AreEqual("old-sword", image["picture"]);
        Assert.That(Convert.ToInt32(image["frames"]), Is.InRange(5, 7));
        Assert.Greater(Convert.ToDouble(image["ms"]), 0.0);
        Assert.AreEqual(image["frames"], m_Ghosting["frames"]);

        // 읽은 것은 다시 나오지 않는다
        yield return Take();
        CollectionAssert.IsEmpty(Images);
        Assert.AreEqual(0.0, m_Ghosting["frames"]);
    }

    // 잔상을 막는 방법들: 그림을 비운다, 숨긴다, 자리 표시 그림으로 바꾼다, 투명하게 한다. 어느 것이든 세지 않는다
    [UnityTest]
    public IEnumerator Ghosting_IgnoresImagesThatHideOrReplaceThePictureWhileLoading()
    {
        yield return Take();
        RawImage cleared = Icon("Cleared", Picture("old-a"));
        RawImage hidden = Icon("Hidden", Picture("old-b"));
        RawImage placeholder = Icon("Placeholder", Picture("old-c"));
        RawImage faded = Icon("Faded", Picture("old-d"));
        var group = faded.gameObject.AddComponent<CanvasGroup>();
        RawImage first = Icon("First", null); // 처음 불러오는 이미지: 이전 그림이 없다
        yield return Frames(2);

        foreach (RawImage icon in new[] { cleared, hidden, placeholder, faded, first }) ImageLoads.Started(icon);
        cleared.texture = null;
        hidden.enabled = false;
        placeholder.texture = Picture("loading");
        group.alpha = 0f;
        yield return Frames(6);
        foreach (RawImage icon in new[] { cleared, hidden, placeholder, faded, first })
        {
            icon.texture = Picture("new");
            icon.enabled = true;
            ImageLoads.Finished(icon);
        }
        group.alpha = 1f;
        yield return Take();

        CollectionAssert.IsEmpty(Images, Json.Write(m_Ghosting));
    }

    // 스프라이트를 쓰는 Image도 같다. 아직 불러오는 중이면 지금까지 센 만큼만 나온다
    [UnityTest]
    public IEnumerator Ghosting_SpritesAndLoadsThatAreStillRunning()
    {
        Texture2D texture = Picture("atlas");
        var sprite = Sprite.Create(texture, new Rect(0, 0, 2, 2), Vector2.zero);
        sprite.name = "old-portrait";
        m_Pictures.Add(sprite);
        var portrait = Element<Image>("Portrait");
        portrait.sprite = sprite;
        yield return Frames(2);
        yield return Take();

        ImageLoads.Started(portrait);
        yield return Frames(4);
        yield return Take();
        Assert.AreEqual("old-portrait", ((Dictionary<string, object>)Images[0])["picture"]);
        Assert.That(Convert.ToInt32(m_Ghosting["frames"]), Is.InRange(3, 5));

        // 그 뒤의 프레임은 다음에 읽을 때 나온다
        yield return Frames(4);
        ImageLoads.Finished(portrait);
        yield return Take();
        Assert.That(Convert.ToInt32(m_Ghosting["frames"]), Is.InRange(3, 5));
    }

    // 불러오는 중에 화면이 닫혀 이미지가 없어져도 탈이 없다
    [UnityTest]
    public IEnumerator Ghosting_SurvivesAnImageDestroyedWhileLoading()
    {
        yield return Take();
        RawImage icon = Icon("Gone", Picture("old"));
        yield return Frames(2);
        ImageLoads.Started(icon);
        UnityEngine.Object.DestroyImmediate(icon.gameObject);
        yield return Frames(3);
        ImageLoads.Finished(icon);
        yield return Take();
        CollectionAssert.IsEmpty(Images);
    }

    IEnumerator RunOnFreshScreen(string yaml)
    {
        SampleShop.Build();
        yield return null;
        m_Result = null;
        yield return RuleRunner.Run(RuleReader.Read(yaml), AgentSession.Local().Call, r => m_Result = r);
    }

    // 샘플에 심은 잔상: 아이템을 바꿔 누르면 새 그림이 오기 전까지 앞 아이템의 그림이 보인다
    [UnityTest]
    public IEnumerator AssertGhosting_CatchesTheStaleIconInTheSampleItemList()
    {
        yield return RunOnFreshScreen(System.IO.File.ReadAllText(Application.dataPath + "/Sample/BuggyRules/ghost-icon.yaml"));

        Assert.IsFalse(m_Result.passed);
        Assert.AreEqual("ghosting", m_Result.code);
        StringAssert.IsMatch("^Canvas/ItemIcon kept showing \"icon-검\" for \\d+ frame\\(s\\) \\(\\d+ ms\\) while loading$", m_Result.message);
    }

    // 처음 고른 아이템은 이전 그림이 없으니 잔상도 없다
    [UnityTest]
    public IEnumerator AssertGhosting_PassesWhenNothingStaleWasShown()
    {
        yield return RunOnFreshScreen(
            "- tapOn: { text: 검, within: { name: ItemList } }\n" +
            "- assertVisible: { name: IconLabel, text: 검 }\n" +
            "- assertGhosting: { frames: 0 }\n");
        Assert.IsTrue(m_Result.passed, m_Result.ToString());

        // 허용하는 프레임 수를 넉넉히 주면 잔상이 있어도 통과한다
        yield return Run(
            "- tapOn: { text: 방패, within: { name: ItemList } }\n" +
            "- assertVisible: { name: IconLabel, text: 방패 }\n" +
            "- assertGhosting: { frames: 100000 }\n");
        Assert.IsTrue(m_Result.passed, m_Result.ToString());
    }

    IEnumerator Run(string yaml)
    {
        m_Result = null;
        yield return RuleRunner.Run(RuleReader.Read(yaml), AgentSession.Local().Call, r => m_Result = r);
    }
}
