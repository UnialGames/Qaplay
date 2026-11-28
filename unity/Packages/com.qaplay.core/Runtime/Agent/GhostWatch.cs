using System.Collections;
using UnityEngine;

namespace Qaplay.Agent
{
    /// <summary>
    /// 게임이 시작될 때 UI 어댑터들을 시작하고(이미지 잔상 감시를 건다), 프레임마다 돌린다. 불러오는 이미지가 없는 동안에는 하는 일이 없다.
    /// 어댑터들은 그보다 먼저(SubsystemRegistration) 스스로 등록되어 있다.
    /// </summary>
    static class GhostWatch
    {
        // 게임 코드가 처음 이미지를 불러오기 전에 걸려 있어야 하므로 씬을 읽기 전에 시작한다
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Start()
        {
            UiSystem ui = UiSystem.Registered;
            foreach (IUiAdapter adapter in ui.Adapters) adapter.Start();
            AgentHost.Run(EveryFrame(ui));
        }

        static IEnumerator EveryFrame(UiSystem ui)
        {
            while (true)
            {
                foreach (IUiAdapter adapter in ui.Adapters) adapter.Tick();
                yield return null;
            }
        }
    }
}
