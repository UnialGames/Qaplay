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
        if (m_Root == null)
        {
            m_Root = new GameObject("LayoutCanvas", typeof(RectTransform), typeof(Canvas), typeof(GraphicRaycaster));
            new GameObject("EventSystem", typeof(UnityEngine.EventSystems.EventSystem)).transform.SetParent(m_Root.transform, false);
        }
        var canvas = m_Root.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 100; // 다른 테스트가 남긴 화면보다 위에 둔다
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

    // 이 테스트가 만든 칸에서 나온 넘침만: "이름 이유"
    static List<string> Issues()
    {
        var found = new List<string>();
        foreach (Dictionary<string, object> issue in UguiLayout.Check(new[] { "overflow" }))
            if (((string)issue["path"]).StartsWith("LayoutCanvas/"))
                found.Add(((string)issue["path"]).Substring("LayoutCanvas/".Length) + " " + issue["reason"]);
        return found;
    }

    // 이 테스트가 만든 요소끼리의 겹침만: "앞 요소~뒤 요소"
    static List<string> Overlaps()
    {
        var found = new List<string>();
        foreach (Dictionary<string, object> issue in UguiLayout.Check(new[] { "overlap" }))
            if (((string)issue["path"]).StartsWith("LayoutCanvas/") && ((string)issue["otherPath"]).StartsWith("LayoutCanvas/"))
                found.Add(((string)issue["path"]).Substring("LayoutCanvas/".Length) + "~" + ((string)issue["otherPath"]).Substring("LayoutCanvas/".Length));
        return found;
    }

    // 화면 가운데에서 (x, y)만큼 떨어진 자리에 w x h 크기의 칸을 만든다. parent를 주면 그 안에 만든다
    RectTransform At(string name, float x, float y, float w, float h, Transform parent = null)
    {
        RectTransform rt = Box(name);
        if (parent != null) rt.SetParent(parent, false);
        rt.anchoredPosition = new Vector2(x, y);
        rt.sizeDelta = new Vector2(w, h);
        return rt;
    }

    RectTransform ButtonAt(string name, float x, float y, float w, float h, Transform parent = null)
    {
        RectTransform rt = At(name, x, y, w, h, parent);
        rt.gameObject.AddComponent<Image>();
        rt.gameObject.AddComponent<Button>();
        return rt;
    }

    Text LabelAt(string name, string text, float x, float y, float w, float h, TextAnchor alignment = TextAnchor.MiddleCenter)
    {
        var label = At(name, x, y, w, h).gameObject.AddComponent<Text>();
        label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        label.fontSize = 20;
        label.alignment = alignment;
        label.text = text;
        return label;
    }

    static IEnumerator Drawn()
    {
        // 그리는 순서(무엇이 위인지)는 한 번 그려진 뒤에 정해진다
        yield return null;
        yield return null;
    }

    [UnityTest]
    public IEnumerator Overlap_ButtonsThatShareArea()
    {
        ButtonAt("A", -100f, 0f, 200f, 60f);
        ButtonAt("B", 50f, 20f, 200f, 60f);   // A와 가로 50, 세로 40이 겹친다
        ButtonAt("Far", 0f, 200f, 200f, 60f);
        ButtonAt("Touching", -300f, 0f, 200f, 60f); // A의 왼쪽에 맞닿아 있을 뿐이다
        yield return Drawn();

        CollectionAssert.AreEquivalent(new[] { "A~B" }, Overlaps());

        Dictionary<string, object> issue = null;
        foreach (Dictionary<string, object> found in UguiLayout.Check(new[] { "overlap" }))
            if ((string)found["path"] == "LayoutCanvas/A") issue = found;
        Assert.AreEqual("overlap", issue["kind"]);
        Assert.AreEqual(50.0 * 40.0 / (200.0 * 60.0), (double)issue["share"], 0.011, "작은 쪽 넓이에서 겹친 비율");
        Assert.AreEqual("B", ((Dictionary<string, object>)issue["otherElement"])["name"]);
    }

    // 글자는 칸이 아니라 실제로 그려진 범위로 견준다
    [UnityTest]
    public IEnumerator Overlap_TextIsMeasuredByItsDrawnGlyphs()
    {
        // 칸은 통째로 겹치지만 글자는 왼쪽 끝과 오른쪽 끝에 떨어져 있다
        LabelAt("Left", "왼쪽", 0f, 0f, 600f, 40f, TextAnchor.MiddleLeft);
        Text right = LabelAt("Right", "오른쪽", 0f, 0f, 600f, 40f, TextAnchor.MiddleRight);
        yield return Drawn();
        CollectionAssert.IsEmpty(Overlaps());

        // 같은 자리에 그려지게 하면 겹친다
        right.alignment = TextAnchor.MiddleLeft;
        yield return Drawn();
        CollectionAssert.AreEquivalent(new[] { "Left~Right" }, Overlaps());
    }

    [UnityTest]
    public IEnumerator Overlap_TextOverAButtonThatIsNotItsOwn()
    {
        RectTransform button = ButtonAt("Buy", 0f, 0f, 200f, 60f);
        // 버튼 자신의 글자는 버튼의 일부다
        var own = At("Text", 0f, 0f, 200f, 60f, button).gameObject.AddComponent<Text>();
        own.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        own.text = "구매";
        own.alignment = TextAnchor.MiddleCenter;
        LabelAt("Badge", "NEW", 90f, 25f, 80f, 30f); // 버튼의 오른쪽 위 모서리에 걸친 다른 글자
        yield return Drawn();

        CollectionAssert.AreEquivalent(new[] { "Buy~Badge" }, Overlaps());
    }

    // 의도된 겹침은 처음부터 보지 않는다: 부모와 자손, 누를 수 없는 그림, 팝업 뒤에 깔린 것
    [UnityTest]
    public IEnumerator Overlap_IgnoresWhatIsMeantToOverlap()
    {
        // 누를 수 있는 칸 안의 버튼(목록의 줄 안에 든 버튼처럼)
        RectTransform row = ButtonAt("Row", 0f, 200f, 400f, 60f);
        ButtonAt("Inner", 100f, 0f, 100f, 40f, row);

        // 버튼에 걸친 장식 그림: 입력을 받지 않는다
        ButtonAt("Decorated", 0f, 100f, 200f, 60f);
        var ribbon = At("Ribbon", 80f, 120f, 80f, 40f).gameObject.AddComponent<Image>();
        ribbon.raycastTarget = false;

        // 화면을 덮는 팝업과 그 안의 버튼: 뒤에 깔린 버튼과 글자는 팝업의 버튼과 겹쳐도 문제가 아니다
        ButtonAt("Behind", 0f, -100f, 200f, 60f);
        LabelAt("BehindLabel", "뒤에 깔린 글자", 0f, -160f, 300f, 40f);
        RectTransform popup = At("Popup", 0f, -130f, 500f, 200f);
        popup.gameObject.AddComponent<Image>();
        ButtonAt("Ok", 0f, 30f, 200f, 60f, popup);                 // Behind와 같은 자리
        var message = At("Message", 0f, -30f, 300f, 40f, popup).gameObject.AddComponent<Text>(); // BehindLabel과 같은 자리
        message.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        message.text = "팝업의 글자";
        message.alignment = TextAnchor.MiddleCenter;
        yield return Drawn();

        CollectionAssert.IsEmpty(Overlaps());
    }

    // 샘플에 심은 겹침: 새로고침을 누르면 뜨는 NEW 배지가 구매 버튼에 걸친다
    [UnityTest]
    public IEnumerator AssertLayout_CatchesTheBadgeOverTheBuyButtonInTheSample()
    {
        yield return RunOnFreshScreen(SampleFile("BuggyRules/overlap-badge.yaml"));

        Assert.IsFalse(m_Result.passed);
        Assert.AreEqual("layout", m_Result.code);
        StringAssert.IsMatch(@"^1 issue\(s\): Canvas/BuyButton overlaps Canvas/NewBadge \(\d+% of the smaller one\)$", m_Result.message);
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
