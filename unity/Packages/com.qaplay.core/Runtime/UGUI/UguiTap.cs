using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Qaplay.UGUI
{
    /// <summary>tap 명령에서 누를 위치를 정하고, 실제로 눌리는 상태인지 검사한다.</summary>
    public static class UguiTap
    {
        static readonly List<RaycastResult> Hits = new List<RaycastResult>();

        /// <summary>
        /// 요소 하나의 중심점을 정규화 좌표(왼쪽 위 원점)로 돌려준다.
        /// 누를 수 없으면 터치를 넣기 전에 not_tappable로 알린다. 이유는 예외의 reason에 담긴다.
        /// </summary>
        public static Vector2 PointOf(UiDump dump, UiSelector selector)
        {
            UiNode node = UiFinder.FindOne(dump, selector).node;
            var point = new Vector2(node.x + node.w / 2f, node.y + node.h / 2f);

            if (point.x < 0f || point.x > 1f || point.y < 0f || point.y > 1f)
                throw NotTappable("off_screen", node.name + " center is outside the screen");
            if (!node.visible)
                throw NotTappable("not_visible", node.name + " is not visible");
            if (!node.interactable)
                throw NotTappable("not_interactable", node.name + " does not accept input");

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
                throw NotTappable("occluded", node.name + " receives no input at its center");
            // 맨 위 요소가 대상의 자손(버튼 안의 글자)이나 조상이면 터치는 대상에 닿는다
            Transform top = Hits[0].gameObject.transform;
            if (!top.IsChildOf(target.transform) && !target.transform.IsChildOf(top))
                throw NotTappable("occluded", node.name + " is covered by " + top.name);
            return point;
        }

        /// <summary>
        /// 누르기 전 자동 대기. 대상이 나타나고 누를 수 있게 될 때까지 매 프레임 다시 검사해, 되는 순간의 위치를 onPoint로 넘긴다.
        /// 제한 시간이 지나면 마지막 검사의 오류(not_found, not_tappable)를 그대로 던진다. 0이면 한 번만 검사한다.
        /// 여러 요소가 걸리는 것(ambiguous)은 기다려도 달라지지 않는 셀렉터 문제로 보고 바로 던진다.
        /// </summary>
        public static IEnumerator WaitForPoint(UiSelector selector, int timeoutMs, Action<Vector2> onPoint)
        {
            if (timeoutMs < 0)
                throw new QaplayException("bad_request", "timeoutMs must be 0 or greater");

            float start = Time.realtimeSinceStartup;
            while (true)
            {
                try
                {
                    onPoint(PointOf(UguiDumper.Dump(), selector));
                    yield break;
                }
                catch (QaplayException e) when ((e.code == "not_found" || e.code == "not_tappable")
                                                && (Time.realtimeSinceStartup - start) * 1000f < timeoutMs)
                {
                    // 아직 누를 수 없다. 다음 프레임에 다시 본다
                }
                yield return null;
            }
        }

        static QaplayException NotTappable(string reason, string message) =>
            new QaplayException("not_tappable", message, reason);
    }
}
