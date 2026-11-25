using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Qaplay;
using Qaplay.Agent;
using Qaplay.Injection;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.TestTools;
using TouchPhase = UnityEngine.InputSystem.TouchPhase;

// 버그 제보: 게임 안에서 제보를 저장하고, 러너가 쓰는 명령(report, reports, reportGet, reportDelete)으로 읽고 지운다
public class ReportTests
{
    readonly AgentSession m_Session = AgentSession.Local();
    Dictionary<string, object> m_Response;
    Keyboard m_Keyboard;

    [SetUp]
    [TearDown]
    public void ClearReports()
    {
        if (Directory.Exists(BugReporter.Folder)) Directory.Delete(BugReporter.Folder, true);
        if (m_Keyboard != null && m_Keyboard.added) InputSystem.RemoveDevice(m_Keyboard);
        m_Keyboard = null;
    }

    IEnumerator Call(string cmd, Dictionary<string, object> args = null)
    {
        m_Response = null;
        yield return m_Session.Call(cmd, args, r => m_Response = r);
    }

    Dictionary<string, object> Result
    {
        get
        {
            Assert.AreEqual(true, m_Response["ok"], Json.Write(m_Response));
            return (Dictionary<string, object>)m_Response["result"];
        }
    }

    string ErrorCode
    {
        get
        {
            Assert.AreEqual(false, m_Response["ok"], Json.Write(m_Response));
            return (string)((Dictionary<string, object>)m_Response["error"])["code"];
        }
    }

    static Dictionary<string, object> Map(object value) => (Dictionary<string, object>)value;
    static Dictionary<string, object> Id(string id) => new Dictionary<string, object> { ["id"] = id };

    [UnityTest]
    public IEnumerator Report_SavesScreenStepsLogAndDeviceInfo()
    {
        SampleShop.Build();
        yield return null;
        // 제보 전의 조작과 로그: 제보에 함께 담겨야 한다
        yield return Call("tap", new Dictionary<string, object> { ["selector"] = Id("shop.buy"), ["timeoutMs"] = 2000 });
        Assert.AreEqual(true, m_Response["ok"], Json.Write(m_Response));
        Debug.Log("[test] marker line before the report");

        yield return Call("report", new Dictionary<string, object> { ["title"] = "골드가 두 번 줄어듦", ["description"] = "구매를 한 번 눌렀다" });
        string id = (string)Result["id"];

        yield return Call("reports");
        var reports = (List<object>)Result["reports"];
        Assert.AreEqual(1, reports.Count);
        Assert.AreEqual(id, Map(reports[0])["id"]);
        Assert.AreEqual("골드가 두 번 줄어듦", Map(reports[0])["title"]);

        yield return Call("reportGet", Id(id));
        Dictionary<string, object> report = Map(Result["report"]);
        Assert.AreEqual("구매를 한 번 눌렀다", report["description"]);
        Assert.AreEqual(Application.platform.ToString(), report["platform"]);
        Assert.AreEqual((double)Screen.width, Map(report["screen"])["width"]);
        Assert.AreEqual(SystemInfo.deviceModel, Map(report["device"])["model"]);
        StringAssert.Contains("- tapOn: { id: shop.buy }", (string)Result["steps"]);
        StringAssert.Contains("marker line before the report", (string)Result["log"]);

        // 그 순간의 화면이 PNG로 들어 있다. 배치 모드는 화면을 그리지 않아 찍지 않는다
        if (Application.isBatchMode) Assert.IsFalse(Result.ContainsKey("screenshot"));
        else
        {
            byte[] png = Convert.FromBase64String((string)Result["screenshot"]);
            CollectionAssert.AreEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, new[] { png[0], png[1], png[2], png[3] });
            Debug.Log("[dbg] report " + id + ": screenshot " + png.Length + " bytes");
        }

        // 조작 이력은 규칙 파일로 읽힌다
        Assert.AreEqual("tap", RuleReader.Read((string)Result["steps"]).steps[0].cmd);

        yield return Call("reportDelete", Id(id));
        Assert.AreEqual(0, Result.Count);
        yield return Call("reports");
        Assert.AreEqual(0, ((List<object>)Result["reports"]).Count);
        yield return Call("reportGet", Id(id));
        Assert.AreEqual("unknown_report", ErrorCode);
    }

    [UnityTest]
    public IEnumerator Report_WithoutTitle_GetsADatedOne_AndBadIdsAreRefused()
    {
        yield return Call("report");
        string id = (string)Result["id"];
        yield return Call("reportGet", Id(id));
        StringAssert.StartsWith("Report 20", (string)Map(Result["report"])["title"]);

        // ID는 폴더 이름으로 쓰인다. 폴더 밖을 가리키는 값은 받지 않는다
        foreach (string bad in new[] { "../" + id, "..", "a/b", "" })
        {
            yield return Call("reportGet", Id(bad));
            Assert.AreEqual("bad_request", ErrorCode, bad);
            yield return Call("reportDelete", Id(bad));
            Assert.AreEqual("bad_request", ErrorCode, bad);
        }
        yield return Call("reportGet");
        Assert.AreEqual("bad_request", ErrorCode);
        Assert.AreEqual(1, BugReporter.List().Count, "잘못된 요청이 제보를 지우지 않았다");
    }

    static IEnumerator WaitForReports(int count, float seconds)
    {
        float start = Time.realtimeSinceStartup;
        while (BugReporter.List().Count < count && Time.realtimeSinceStartup - start < seconds) yield return null;
    }

    // 테스터가 PC에서 F12를 누르면 제보가 저장된다
    [UnityTest]
    public IEnumerator F12_SavesAReport()
    {
        SampleShop.Build();
        yield return null;
        yield return TouchInjector.Tap(0.5f, 0.2f); // 가상 장치의 입력이 창 포커스와 무관하게 게임으로 가게 한다
        m_Keyboard = InputSystem.AddDevice<Keyboard>();
        yield return null;

        InputSystem.QueueStateEvent(m_Keyboard, new KeyboardState(Key.F12));
        yield return null;
        InputSystem.QueueStateEvent(m_Keyboard, new KeyboardState());
        yield return WaitForReports(1, 5f);

        List<Dictionary<string, object>> reports = BugReporter.List();
        Assert.AreEqual(1, reports.Count);
        StringAssert.StartsWith("Report 20", (string)reports[0]["title"]);
    }

    // 기기에서는 세 손가락으로 1초 누르면 제보가 저장된다. 누르고 있는 동안 한 번만
    [UnityTest]
    public IEnumerator ThreeFingerHold_SavesOneReport()
    {
        SampleShop.Build();
        yield return null;
        yield return TouchInjector.Tap(0.5f, 0.2f); // 가상 터치스크린을 만든다
        Touchscreen screen = Touchscreen.current;

        void Touch(int finger, TouchPhase phase) => InputSystem.QueueStateEvent(screen, new TouchState
        {
            touchId = 920000 + finger, phase = phase, pressure = phase == TouchPhase.Ended ? 0f : 1f,
            position = new Vector2(Screen.width * (0.3f + 0.2f * finger), Screen.height * 0.8f),
        });

        // 두 손가락으로는 제보되지 않는다
        Touch(0, TouchPhase.Began);
        Touch(1, TouchPhase.Began);
        yield return WaitForReports(1, 1.5f);
        Assert.AreEqual(0, BugReporter.List().Count);

        Touch(2, TouchPhase.Began);
        yield return WaitForReports(1, 3f);
        Assert.AreEqual(1, BugReporter.List().Count);
        // 계속 누르고 있어도 더 생기지 않는다
        yield return WaitForReports(2, 1.5f);
        Assert.AreEqual(1, BugReporter.List().Count);

        for (int finger = 0; finger < 3; finger++) Touch(finger, TouchPhase.Ended);
        yield return null;
        yield return null;
    }
}
