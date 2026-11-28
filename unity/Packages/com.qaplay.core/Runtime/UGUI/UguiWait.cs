using System.Collections;

namespace Qaplay.UGUI
{
    /// <summary>wait 명령(uGUI만 볼 때). 화면이 기다리는 상태가 될 때까지 매 프레임 검사한다.</summary>
    public static class UguiWait
    {
        /// <summary>
        /// 조건이 이미 만족되어 있으면 프레임을 넘기지 않고 끝난다.
        /// 제한 시간이 지나면 timeout 예외를 던진다. 예외의 count는 마지막에 걸린 요소 수.
        /// </summary>
        public static IEnumerator Until(UiSelector selector, string state, int timeoutMs) => UiWait.Until(UguiDumper.Dump, selector, state, timeoutMs);
    }
}
