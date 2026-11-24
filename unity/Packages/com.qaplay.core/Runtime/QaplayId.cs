using UnityEngine;

namespace Qaplay
{
    /// <summary>
    /// UI 요소에 고유 ID를 붙인다. 붙이면 dump의 id에 나오고 셀렉터의 id 조건으로 찾을 수 있다.
    /// 이름이나 글자가 바뀌어도 테스트가 깨지지 않게 하고 싶은 요소에만 선택해서 쓴다.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("qaplay/Qaplay Id")]
    public sealed class QaplayId : MonoBehaviour
    {
        // ponytail: 같은 ID가 여러 요소에 붙어도 막지 않는다. 그런 ID로 누르려 하면 ambiguous가 나온다.
        // 목록 아이템처럼 프리팹으로 찍어 낸 요소는 within이나 index로 구분한다.
        public string id;
    }
}
