using System.Collections;
using Qaplay.UGUI;
using UnityEngine;

namespace Qaplay.Agent
{
    /// <summary>게임이 시작될 때부터 잔상 감지(UguiGhosts)를 프레임마다 돌린다. 불러오는 이미지가 없는 동안에는 하는 일이 없다.</summary>
    static class GhostWatch
    {
        // 게임 코드가 처음 이미지를 불러오기 전에 걸려 있어야 하므로 씬을 읽기 전에 시작한다
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Start()
        {
            UguiGhosts.Hook();
            AgentHost.Run(EveryFrame());
        }

        static IEnumerator EveryFrame()
        {
            while (true)
            {
                UguiGhosts.Tick();
                yield return null;
            }
        }
    }
}
