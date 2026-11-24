using System.Collections;
using UnityEngine;

namespace Qaplay.UGUI
{
    /// <summary>wait 명령. 화면이 기다리는 상태가 될 때까지 매 프레임 검사한다.</summary>
    public static class UguiWait
    {
        /// <summary>
        /// 조건이 이미 만족되어 있으면 프레임을 넘기지 않고 끝난다.
        /// 제한 시간이 지나면 timeout 예외를 던진다. 예외의 count는 마지막에 걸린 요소 수.
        /// </summary>
        public static IEnumerator Until(UiSelector selector, string state, int timeoutMs)
        {
            if (timeoutMs < 0)
                throw new QaplayException("bad_request", "timeoutMs must be 0 or greater");

            // 게임이 멈춰 있어도(timeScale 0) 흐르는 시간으로 잰다
            float start = Time.realtimeSinceStartup;
            // ponytail: 매 프레임 화면 전체를 dump한다. 요소가 많아 느려지면 셀렉터에 걸리는 것만 훑도록 바꾼다.
            int count;
            while (!UiWait.IsMet(UguiDumper.Dump(), selector, state, out count))
            {
                if ((Time.realtimeSinceStartup - start) * 1000f >= timeoutMs)
                    throw new QaplayException("timeout",
                        "not " + state + " after " + timeoutMs + " ms (" + count + " matched)", count: count);
                yield return null;
            }
        }
    }
}
