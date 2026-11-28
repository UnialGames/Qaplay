using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Qaplay.UGUI
{
    /// <summary>tap 명령에서 누를 위치를 정하고, 실제로 눌리는 상태인지 검사한다(uGUI만 볼 때).</summary>
    public static class UguiTap
    {
        static readonly List<RaycastResult> Hits = new List<RaycastResult>();

        /// <summary>
        /// 요소 하나의 중심점을 정규화 좌표(왼쪽 위 원점)로 돌려준다.
        /// 누를 수 없으면 터치를 넣기 전에 not_tappable로 알린다. 이유는 예외의 reason에 담긴다.
        /// </summary>
        public static Vector2 PointOf(UiDump dump, UiSelector selector) => UiTap.PointOf(dump, selector, CheckHit);

        /// <summary>
        /// uGUI의 요소를 그 자리에서 누르면 터치가 그 요소에 닿는지: EventSystem으로 그 자리를 쏴서 맨 위 요소가 대상이나 대상의 안팎인지 본다.
        /// </summary>
        public static void CheckHit(UiDump dump, UiNode node, Vector2 point)
        {
            EventSystem eventSystem = EventSystem.current;
            if (eventSystem == null)
                throw new QaplayException("internal", "no EventSystem in the scene");
            var target = node.handle as GameObject;
            if (target == null)
                throw new QaplayException("not_found", node.name + " no longer exists");

            // ponytail: 중심점 한 곳만 검사한다. 중심은 가려졌지만 가장자리는 보이는 요소도 occluded가 된다.
            var pointer = new PointerEventData(eventSystem)
            {
                position = new Vector2(point.x * dump.screenWidth, (1f - point.y) * dump.screenHeight)
            };
            eventSystem.RaycastAll(pointer, Hits);
            // 그 자리에서 입력을 받는 요소가 없으면(중심이 마스크에 잘린 아이템 등) 터치는 대상에 닿지 않는다
            if (Hits.Count == 0)
                throw UiTap.NotTappable("occluded", node.name + " receives no input at its center");
            // 맨 위 요소가 대상의 자손(버튼 안의 글자)이나 조상이면 터치는 대상에 닿는다
            Transform top = Hits[0].gameObject.transform;
            if (!top.IsChildOf(target.transform) && !target.transform.IsChildOf(top))
                throw UiTap.NotTappable("occluded", node.name + " is covered by " + top.name);
        }

        /// <summary>누르기 전 자동 대기(uGUI만 볼 때). UiTap.WaitForPoints와 같다.</summary>
        public static IEnumerator WaitForPoints(IReadOnlyList<UiSelector> selectors, int timeoutMs, Action<Vector2[]> onPoints) =>
            UiTap.WaitForPoints(UguiDumper.Dump, PointOf, selectors, timeoutMs, onPoints);
    }
}
