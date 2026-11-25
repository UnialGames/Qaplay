using System.Collections;
using UnityEngine;

namespace Qaplay.Agent
{
    /// <summary>
    /// 에이전트의 코루틴을 게임 프레임에 맞춰 돌리는 숨은 게임오브젝트.
    /// 탭은 누름과 뗌 사이에 게임 프레임이 지나가야 하므로, 에디터 창처럼 게임 밖에서 시작한 일도 여기서 돌린다.
    /// </summary>
    public sealed class AgentHost : MonoBehaviour
    {
        static AgentHost s_Instance;

        /// <summary>Play 중에만 부른다. 씬이 바뀌어도 끊기지 않는다.</summary>
        public static Coroutine Run(IEnumerator routine)
        {
            if (s_Instance == null)
            {
                var go = new GameObject("qaplay") { hideFlags = HideFlags.HideAndDontSave };
                DontDestroyOnLoad(go);
                s_Instance = go.AddComponent<AgentHost>();
            }
            return s_Instance.StartCoroutine(routine);
        }
    }
}
