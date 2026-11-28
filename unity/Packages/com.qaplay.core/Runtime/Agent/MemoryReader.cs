using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Profiling;
using UnityEngine.Profiling;

namespace Qaplay.Agent
{
    /// <summary>memory 명령. 게임이 지금 쓰고 있는 메모리를 세 계층(바이트)으로 읽는다. protocol/README.md의 memory.</summary>
    public static class MemoryReader
    {
        // OS가 본 프로세스 전체의 메모리. 프레임이 끝날 때 갱신되는 카운터라 처음 읽을 때 켜서 계속 둔다
        static ProfilerRecorder s_Process;

        /// <summary>돌려받은 코루틴을 끝까지 돌리면(한 프레임) result에 managed, native, process가 담긴다.</summary>
        public static IEnumerator Read(Dictionary<string, object> result)
        {
            if (!s_Process.Valid) s_Process = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "System Used Memory");
            // 곧 치워질 쓰레기는 쓰고 있는 메모리가 아니다. 먼저 치워야 잴 때마다 같은 기준이 된다.
            // 쓰지 않는 에셋은 내리지 않는다: 없애지 않고 버린 텍스처 같은 누수가 숫자에 남아야 한다
            GC.Collect();
            yield return null; // 카운터가 수집 뒤의 값으로 갱신되기를 기다린다

            result["managed"] = GC.GetTotalMemory(false);
            result["native"] = Profiler.GetTotalAllocatedMemoryLong();
            // 카운터가 없는 플랫폼에서는 값이 0으로 남는다
            result["process"] = s_Process.Valid && s_Process.LastValue > 0 ? (object)s_Process.LastValue : null;
        }
    }
}
