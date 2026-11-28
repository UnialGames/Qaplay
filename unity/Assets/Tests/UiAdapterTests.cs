using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Qaplay;
using Qaplay.Agent;
using UnityEngine;
using UnityEngine.TestTools;

// 에이전트는 UI 시스템을 모르고 UI 어댑터만 본다. 가짜 어댑터 하나를 uGUI 옆에 끼워, 두 UI 시스템이 함께 있는 프로젝트처럼 돌려 본다
public class UiAdapterTests
{
    /// <summary>화면 오른쪽 아래에 버튼 하나가 있는 가짜 UI 시스템. 누를 수 있는지 물으면 적어 둔다.</summary>
    sealed class FakeAdapter : IUiAdapter
    {
        public readonly List<string> asked = new List<string>();
        public bool covered;

        public string Name => "fake";

        public void Dump(UiDump dump)
        {
            var root = new UiNode { name = "FakePanel", type = "Panel", x = 0.8f, y = 0.8f, w = 0.2f, h = 0.2f, visible = true };
            root.children.Add(new UiNode { name = "FakeButton", type = "Button", id = "fake.ok", x = 0.85f, y = 0.85f, w = 0.1f, h = 0.1f, visible = true, interactable = true });
            dump.roots.Add(root);
        }

        public void CheckTappable(UiDump dump, UiNode node, Vector2 point)
        {
            asked.Add(node.name);
            if (covered) throw UiTap.NotTappable("occluded", node.name + " is covered by FakeCover");
        }

        public List<object> Layout(ICollection<string> checks) =>
            new List<object> { new Dictionary<string, object> { ["kind"] = "overflow", ["path"] = "FakePanel/FakeButton" } };

        public void Start() { }
        public void Tick() { }
        public List<object> TakeGhosts() => new List<object>();
        public void ListenHistory() { }
        public List<string> History() => new List<string> { "- tapOn: { id: fake.ok }" };
    }

    [UnityTest]
    public IEnumerator TwoUiSystems_AreMergedAndEachAnswersForItsOwnElements()
    {
        SampleShop.Build();
        yield return null;
        var fake = new FakeAdapter();
        var adapters = new List<IUiAdapter>(UiAdapters.All) { fake };
        var ui = new UiSystem(adapters);

        // uGUI가 스스로 등록되어 있다(에이전트는 uGUI 어셈블리를 참조하지 않는다)
        CollectionAssert.AreEqual(new[] { "ugui" }, UiSystem.Registered.Names);
        CollectionAssert.AreEqual(new[] { "ugui", "fake" }, ui.Names);

        // 요소 트리는 둘을 합친다. 셀렉터는 어느 UI 시스템의 요소든 같게 찾는다
        UiDump dump = ui.Dump();
        Assert.AreEqual(1, UiFinder.Find(dump, new UiSelector { id = "shop.buy" }).Count);
        Assert.AreEqual(1, UiFinder.Find(dump, new UiSelector { id = "fake.ok" }).Count);

        // 누를 수 있는지는 그 요소를 낸 어댑터가 답한다
        Vector2 point = ui.PointOf(dump, new UiSelector { id = "fake.ok" });
        Assert.AreEqual(0.9f, point.x, 0.0001f);
        CollectionAssert.AreEqual(new[] { "FakeButton" }, fake.asked);
        ui.PointOf(dump, new UiSelector { id = "shop.buy" });
        Assert.AreEqual(1, fake.asked.Count, "a uGUI element is not asked to the fake adapter");
        fake.covered = true;
        var e = Assert.Throws<QaplayException>(() => ui.PointOf(dump, new UiSelector { id = "fake.ok" }));
        Assert.AreEqual("not_tappable", e.code);
        Assert.AreEqual("occluded", e.reason);

        // 배치 문제와 조작 이력도 어댑터마다 모은다
        Assert.IsTrue(ui.Layout(null).Exists(issue => "FakePanel/FakeButton".Equals(((Dictionary<string, object>)issue)["path"])));
        CollectionAssert.Contains(ui.History(), "- tapOn: { id: fake.ok }");

        // 그 UI로 만든 세션: hello가 두 UI 시스템을 알리고, 명령이 그 UI를 쓴다
        AgentSession session = AgentSession.Local(ui);
        Dictionary<string, object> response = null;
        yield return session.Call("hello", new Dictionary<string, object> { ["protocol"] = new Dictionary<string, object> { ["min"] = 1.0, ["max"] = 1.0 } }, r => response = r);
        CollectionAssert.AreEqual(new object[] { "ugui", "fake" }, (IEnumerable)((Dictionary<string, object>)response["result"])["ui"]);
        yield return session.Call("find", new Dictionary<string, object> { ["selector"] = new Dictionary<string, object> { ["name"] = "FakeButton" } }, r => response = r);
        Assert.AreEqual(1, ((List<object>)((Dictionary<string, object>)response["result"])["matches"]).Count, Json.Write(response));
    }
}
