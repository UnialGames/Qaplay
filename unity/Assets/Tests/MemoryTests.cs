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
