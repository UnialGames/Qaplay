using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Scripting;

// 아무도 참조하지 않는 어셈블리는 IL2CPP 빌드에서 통째로 빠진다(코드 줄이기). 에이전트가 이 어셈블리를 참조하지 않으므로
// 이 표시가 없으면 IL2CPP 빌드에서 uGUI 어댑터가 등록되지 않아 모든 요소가 not_found가 됐다(Mono 빌드는 괜찮았다).
// UI 어댑터를 새로 만들 때도 그 어셈블리에 이 표시를 붙인다
[assembly: AlwaysLinkAssembly]

namespace Qaplay.UGUI
{
    /// <summary>uGUI 어댑터. 이 어셈블리가 읽히면 스스로 등록한다. 에이전트는 이 어셈블리를 참조하지 않는다.</summary>
    [Preserve]
    public sealed class UguiAdapter : IUiAdapter
    {
        // 씬을 읽기 전에 에이전트가 어댑터들을 시작하므로, 그보다 먼저(가장 이른 때) 등록한다
        [Preserve]
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Register() => UiAdapters.Register(new UguiAdapter());

        public string Name => "ugui";

        public void Dump(UiDump dump) => dump.roots.AddRange(UguiDumper.Dump(dump.screenWidth, dump.screenHeight).roots);

        public void CheckTappable(UiDump dump, UiNode node, Vector2 point) => UguiTap.CheckHit(dump, node, point);

        public List<object> Layout(ICollection<string> checks) => UguiLayout.Check(checks);

        public void Start() => UguiGhosts.Hook();

        public void Tick() => UguiGhosts.Tick();

        public List<object> TakeGhosts() => UguiGhosts.Take();

        public void ListenHistory() => UguiRecorder.Listen();

        public List<string> History() => UguiRecorder.History();
    }
}
