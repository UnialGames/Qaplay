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
    }
}
