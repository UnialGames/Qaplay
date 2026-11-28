using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using NUnit.Framework;
using Qaplay;
using Qaplay.Agent;
using UnityEngine;
using UnityEngine.TestTools;

// memory 명령과 규칙의 assertMemory. 에디터에서 재므로 값에는 에디터 자신의 메모리도 들어 있다. 그래서 늘어난 양으로 본다.
public class MemoryTests
{
    const double MB = 1024.0 * 1024.0;

    Dictionary<string, object> m_Memory;
    RuleResult m_Result;

    IEnumerator ReadMemory()
    {
        Dictionary<string, object> response = null;
        yield return AgentSession.Local().Call("memory", null, r => response = r);
        Assert.AreEqual(true, response["ok"], Json.Write(response));
        m_Memory = (Dictionary<string, object>)response["result"];
    }

    double Megabytes(string layer) => Convert.ToDouble(m_Memory[layer]) / MB;

    IEnumerator Run(string yaml)
    {
        m_Result = null;
        yield return RuleRunner.Run(RuleReader.Read(yaml), AgentSession.Local().Call, r => m_Result = r);
    }

    [UnityTest]
    public IEnumerator Memory_ReturnsThreeLayersInBytes()
    {
        yield return ReadMemory();

        Assert.AreEqual(3, m_Memory.Count, Json.Write(m_Memory));
        Assert.Greater(Megabytes("managed"), 1.0);
        Assert.Greater(Megabytes("native"), 1.0);
        // 프로세스 전체는 그 안의 C# 힙보다 크다
        Assert.Greater(Megabytes("process"), Megabytes("managed"));
    }

    [UnityTest]
    public IEnumerator Memory_ManagedCountsWhatIsHeldButNotGarbage()
    {
        yield return ReadMemory();
        double before = Megabytes("managed");

        // 버린 것(쓰레기) 64MB: 재기 전에 치우므로 세지 않는다
        MakeGarbage();
        yield return ReadMemory();
        Assert.Less(Megabytes("managed") - before, 16.0, "쓰레기는 세지 않는다");

        // 쥐고 있는 것 32MB: 그만큼 늘어난다
        var held = new byte[32 * 1024 * 1024];
        yield return ReadMemory();
        Assert.Greater(Megabytes("managed") - before, 30.0, "쥐고 있는 것은 센다");
        GC.KeepAlive(held);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void MakeGarbage()
    {
        for (int n = 0; n < 64; n++) GC.KeepAlive(new byte[1024 * 1024]);
    }

    // 없애지 않고 버린 텍스처(가장 흔한 누수)가 native에 그대로 남아야 한다
    [UnityTest]
    public IEnumerator Memory_NativeKeepsATextureThatWasDroppedWithoutDestroy()
    {
        yield return ReadMemory();
        double before = Megabytes("native");

        var texture = new Texture2D(2048, 2048, TextureFormat.RGBA32, false); // 16MB
        int id = texture.GetInstanceID();
        texture = null; // 참조만 버리고 Destroy하지 않았다
        yield return ReadMemory();
        Assert.Greater(Megabytes("native") - before, 12.0, "버린 텍스처가 남아 있어야 한다");

        UnityEngine.Object.DestroyImmediate(Resources.InstanceIDToObject(id));
        yield return ReadMemory();
        Assert.Less(Megabytes("native") - before, 4.0, "없애면 돌아온다");
    }

    static string SampleFile(string path) => System.IO.File.ReadAllText(Application.dataPath + "/Sample/" + path);

    static object Gold => GameValueReader.Get(new[] { "gold" })["gold"];

    IEnumerator RunOnFreshScreen(string yaml)
    {
        SampleShop.Build();
        yield return null;
        yield return Run(yaml);
    }

    // 샘플에 심은 누수: 보상 팝업은 열 때마다 1MB 텍스처를 만들고 없애지 않는다
    [UnityTest]
    public IEnumerator LeakCheck_CatchesTheLeakInTheSampleRewardPopup()
    {
        yield return RunOnFreshScreen(SampleFile("BuggyRules/leak-reward.yaml"));

        Assert.IsFalse(m_Result.passed);
        Assert.AreEqual("leak", m_Result.code);
        Assert.AreEqual("- leakCheck: 20", m_Result.step.source);
        Assert.AreEqual(1, m_Result.index);
        StringAssert.IsMatch(@"^native grew 1(\.\d+)? MB per repeat \(\d+\.\d MB -> \d+\.\d MB over 20 repeats, allowed 0\.05 MB per repeat\)$",
            m_Result.message);
        Assert.AreEqual(20, m_Result.memory.Count, "되풀이마다 한 번 잰다");
        Debug.Log("[dbg] leaking popup: managed " + m_Result.growth["managed"] + ", native " + m_Result.growth["native"] + " MB per repeat");
    }

    [UnityTest]
    public IEnumerator LeakCheck_PassesOnAPopupThatDoesNotLeak()
    {
        yield return RunOnFreshScreen(SampleFile("Rules/leak-info.yaml"));

        Assert.IsTrue(m_Result.passed, m_Result.ToString());
        Assert.AreEqual(20, m_Result.memory.Count);
        Assert.Less(m_Result.growth["native"], RuleReader.DefaultLeakAllowMb);
        Debug.Log("[dbg] clean popup: managed " + m_Result.growth["managed"] + ", native " + m_Result.growth["native"] + " MB per repeat");
    }

    // C# 쪽 누수: 누를 때마다 1MB를 목록에 쥐고 놓지 않는 버튼
    [UnityTest]
    public IEnumerator LeakCheck_CatchesAManagedLeak()
    {
        SampleShop.Build();
        yield return null;
        var held = new List<byte[]>();
        GameObject.Find("RefreshButton").GetComponent<UnityEngine.UI.Button>().onClick.AddListener(() => held.Add(new byte[1024 * 1024]));
        yield return Run("- leakCheck: 10\n- tapOn: { name: RefreshButton }\n");

        Assert.AreEqual("leak", m_Result.code, m_Result.ToString());
        StringAssert.IsMatch(@"^managed grew (0\.9\d*|1(\.\d+)?) MB per repeat \(", m_Result.message);
        Assert.AreEqual(10, held.Count);
    }

    // leakCheck 위의 명령은 한 번만 실행하고(준비), 아래의 명령만 되풀이한다
    [UnityTest]
    public IEnumerator LeakCheck_RunsTheCommandsAboveItOnce()
    {
        yield return RunOnFreshScreen(
            "- tapOn: { id: shop.buy }\n" +
            "- leakCheck: 10\n" +
            "- tapOn: { name: InfoButton }\n" +
            "- tapOn: { text: 닫기, within: { name: InfoPopup } }\n");

        Assert.IsTrue(m_Result.passed, m_Result.ToString());
        Assert.AreEqual(1100, Gold, "구매는 한 번만");
        Assert.AreEqual(10, m_Result.memory.Count);
    }

    // 되풀이하는 중에 명령이 실패하면 보통의 실패이고, 몇 번째 되풀이였는지 알린다
    [UnityTest]
    public IEnumerator LeakCheck_ACommandFailingInARepeat_SaysWhichRepeat()
    {
        yield return RunOnFreshScreen(
            "- leakCheck: 10\n" +
            "- tapOn: { id: shop.buy }\n" +
            "- assertValue: { gold: 1100 }\n");

        Assert.IsFalse(m_Result.passed);
        Assert.AreEqual("value_mismatch", m_Result.code);
        Assert.AreEqual(3, m_Result.index);
        Assert.AreEqual("gold: expected 1100, got 1000 (repeat 2 of 10)", m_Result.message);
        Assert.AreEqual(1, m_Result.memory.Count, "끝난 되풀이만 잰다");
        Assert.IsNull(m_Result.growth);
    }

    [UnityTest]
    public IEnumerator AssertMemory_PassesInsideTheBudgetAndFailsOverIt()
    {
        yield return Run("- assertMemory: { managed: 1000000, native: 1000000, process: 1000000 }");
        Assert.IsTrue(m_Result.passed, m_Result.ToString());

        // 넘은 계층만 알린다
        yield return Run("- assertMemory: { managed: 0.5, process: 1000000 }");
        Assert.IsFalse(m_Result.passed);
        Assert.AreEqual("over_budget", m_Result.code);
        StringAssert.IsMatch(@"^managed: \d+\.\d MB, budget 0\.5 MB$", m_Result.message);
        StringAssert.StartsWith("failed at command 1 (line 1): - assertMemory: { managed: 0.5, process: 1000000 } -> over_budget: managed: ",
            m_Result.ToString());
    }
}
