using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Qaplay;
using Qaplay.Agent;
using Qaplay.UGUI;
using TMPro;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;

// layout 명령과 규칙의 assertLayout: 글자가 자기 칸에 다 들어가는지.
public class LayoutTests
{
    GameObject m_Root;
    RuleResult m_Result;

    [TearDown]
    public void Cleanup()
    {
        if (m_Root != null) Object.DestroyImmediate(m_Root);
    }

    // 가로 400, 세로 60짜리 칸 하나를 화면 가운데에 만든다
    RectTransform Box(string name)
    {
        if (m_Root == null) m_Root = new GameObject("LayoutCanvas", typeof(RectTransform), typeof(Canvas));
        m_Root.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(m_Root.transform, false);
        var rt = (RectTransform)go.transform;
        rt.sizeDelta = new Vector2(400f, 60f);
        return rt;
    }

    Text Legacy(string name, string text, int fontSize)
    {
        var label = Box(name).gameObject.AddComponent<Text>();
        label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        label.fontSize = fontSize;
        label.text = text;
        return label;
    }

    TMP_Text Tmp(string name, string text, float fontSize)
    {
        var label = Box(name).gameObject.AddComponent<TextMeshProUGUI>();
        label.fontSize = fontSize;
        label.text = text;
        return label;
    }

    // 이 테스트가 만든 칸에서 나온 문제만: "이름 이유"
    static List<string> Issues()
    {
        var found = new List<string>();
        foreach (Dictionary<string, object> issue in UguiLayout.Check())
            if (((string)issue["path"]).StartsWith("LayoutCanvas/"))
                found.Add(((string)issue["path"]).Substring("LayoutCanvas/".Length) + " " + issue["reason"]);
        return found;
    }

    const string Long = "이 글자는 칸에 다 들어가지 않을 만큼 아주 길게 적어 둔 문장입니다";

    [UnityTest]
    public IEnumerator Overflow_LegacyText()
    {
        Legacy("Fits", "짧은 글자", 20);
        Text cut = Legacy("Cut", Long, 40);                       // 기본: 줄을 바꾸고 세로로 넘치면 자른다
        Text spillWide = Legacy("SpillWide", Long, 40);
        spillWide.horizontalOverflow = HorizontalWrapMode.Overflow;
        Text spillTall = Legacy("SpillTall", Long, 40);
        spillTall.verticalOverflow = VerticalWrapMode.Overflow;
        Text shrunk = Legacy("Shrunk", "조금 긴 글자입니다", 60);   // 맞춤 크기로 줄어들어 다 들어간다
        shrunk.resizeTextForBestFit = true;
        shrunk.resizeTextMinSize = 8;
        Text tooLong = Legacy("TooLong", Long + Long + Long + Long, 60); // 맞춤 크기의 최소로도 들어가지 않는다
        tooLong.resizeTextForBestFit = true;
        tooLong.resizeTextMinSize = 30;
        Legacy("Empty", "", 40);
        yield return null;

        CollectionAssert.AreEquivalent(
            new[] { "Cut truncated", "SpillWide spilled", "SpillTall spilled", "TooLong truncated" }, Issues());
    }

    [UnityTest]
    public IEnumerator Overflow_TextMeshPro()
    {
        Tmp("Fits", "SHORT", 24f);
        TMP_Text ellipsis = Tmp("Ellipsis", "THIS TEXT IS FAR TOO LONG FOR ITS BOX", 48f);
        ellipsis.textWrappingMode = TextWrappingModes.NoWrap;
        ellipsis.overflowMode = TextOverflowModes.Ellipsis;
        TMP_Text truncate = Tmp("Truncate", "THIS TEXT IS FAR TOO LONG FOR ITS BOX AND WRAPS TO MANY LINES", 48f);
        truncate.overflowMode = TextOverflowModes.Truncate;
        TMP_Text spill = Tmp("Spill", "THIS TEXT IS FAR TOO LONG FOR ITS BOX", 48f);
        spill.textWrappingMode = TextWrappingModes.NoWrap;
        spill.overflowMode = TextOverflowModes.Overflow;
        TMP_Text autoSize = Tmp("AutoSize", "THIS TEXT SHRINKS TO FIT", 48f);
        autoSize.enableAutoSizing = true;
        autoSize.fontSizeMin = 4f;
        yield return null;

        CollectionAssert.AreEquivalent(new[] { "Ellipsis truncated", "Truncate truncated", "Spill spilled" }, Issues());
    }

    // 보이지 않는 글자는 보지 않는다: 화면 밖, 투명, 스크롤 영역에 잘린 것
    [UnityTest]
    public IEnumerator Overflow_IgnoresTextNobodyCanSee()
    {
        Text offscreen = Legacy("Offscreen", Long, 40);
        offscreen.rectTransform.anchoredPosition = new Vector2(100000f, 0f);
        Text faded = Legacy("Faded", Long, 40);
        faded.gameObject.AddComponent<CanvasGroup>().alpha = 0f;
        yield return null;

        CollectionAssert.IsEmpty(Issues());
    }

    IEnumerator RunOnFreshScreen(string yaml)
    {
        SampleShop.Build();
        yield return null;
        m_Result = null;
        yield return RuleRunner.Run(RuleReader.Read(yaml), AgentSession.Local().Call, r => m_Result = r);
    }

    static string SampleFile(string path) => System.IO.File.ReadAllText(Application.dataPath + "/Sample/" + path);

    // 샘플의 첫 화면에는 넘치는 글자가 없다
    [UnityTest]
    public IEnumerator AssertLayout_PassesOnTheSampleShop()
    {
        yield return RunOnFreshScreen(SampleFile("Rules/layout.yaml"));
        Assert.IsTrue(m_Result.passed, m_Result.ToString());
    }

    // 샘플에 심은 넘침: 이벤트 패널의 배너는 잘리고 공지는 삐져나온다
    [UnityTest]
    public IEnumerator AssertLayout_CatchesTheOverflowsInTheSampleEventPanel()
    {
        yield return RunOnFreshScreen(SampleFile("BuggyRules/overflow-event.yaml"));

        Assert.IsFalse(m_Result.passed);
        Assert.AreEqual("layout", m_Result.code);
        Assert.AreEqual(
            "2 issue(s): text is cut off in Canvas/EventPanel/Banner \"LIMITED TIME SUPER SALE - EVERYTHING MUST GO TODAY\"; " +
            "text spills out of Canvas/EventPanel/Notice \"오늘 하루만 전설의 검과 방패와 투구를 모두 90% 할인합니다\"",
            m_Result.message);
    }

    [UnityTest]
    public IEnumerator Layout_RejectsAnUnknownCheck()
    {
        Dictionary<string, object> response = null;
        yield return AgentSession.Local().Call("layout",
            new Dictionary<string, object> { ["checks"] = new List<object> { "colors" } }, r => response = r);
        Assert.AreEqual("bad_request", ((Dictionary<string, object>)response["error"])["code"]);
    }
}
