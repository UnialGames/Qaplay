namespace Qaplay
{
    /// <summary>wait 명령의 조건 검사. protocol/README.md의 wait.</summary>
    public static class UiWait
    {
        /// <summary>
        /// 화면이 기다리는 상태인지 검사한다. visible: 걸린 요소 중 보이는 것이 하나 이상. gone: 보이는 것이 없음.
        /// count에는 보이는지와 무관하게 셀렉터에 걸린 요소 수가 담긴다(timeout 오류에 싣는 값).
        /// </summary>
        public static bool IsMet(UiDump dump, UiSelector selector, string state, out int count)
        {
            if (state != "visible" && state != "gone")
                throw new QaplayException("bad_request", "state must be visible or gone");

            count = 0;
            int visible = 0;
            try
            {
                foreach (UiMatch m in UiFinder.Find(dump, selector))
                {
                    count++;
                    if (m.node.visible) visible++;
                }
            }
            catch (QaplayException e) when (e.code == "not_found")
            {
                // within이 가리키는 요소(팝업 등)가 아직 없으면 그 안의 요소도 없는 것이다. 오류가 아니라 0개로 본다.
            }
            return state == "visible" ? visible > 0 : visible == 0;
        }

        /// <summary>
        /// wait 명령. 화면(dumpNow)이 기다리는 상태가 될 때까지 매 프레임 검사한다. 이미 만족되어 있으면 프레임을 넘기지 않고 끝난다.
        /// 제한 시간이 지나면 timeout 예외를 던진다. 예외의 count는 마지막에 걸린 요소 수.
        /// </summary>
        public static System.Collections.IEnumerator Until(System.Func<UiDump> dumpNow, UiSelector selector, string state, int timeoutMs)
        {
            if (timeoutMs < 0)
                throw new QaplayException("bad_request", "timeoutMs must be 0 or greater");

            // 게임이 멈춰 있어도(timeScale 0) 흐르는 시간으로 잰다
            float start = UnityEngine.Time.realtimeSinceStartup;
            // ponytail: 매 프레임 화면 전체를 dump한다. 요소가 많아 느려지면 셀렉터에 걸리는 것만 훑도록 바꾼다.
            int count;
            while (!IsMet(dumpNow(), selector, state, out count))
            {
                if ((UnityEngine.Time.realtimeSinceStartup - start) * 1000f >= timeoutMs)
                    throw new QaplayException("timeout",
                        "not " + state + " after " + timeoutMs + " ms (" + count + " matched)", count: count);
                yield return null;
            }
        }
    }
}
