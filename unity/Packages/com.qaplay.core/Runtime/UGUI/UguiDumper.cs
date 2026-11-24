using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Qaplay.UGUI
{
    /// <summary>uGUI 화면을 UiNode 트리로 뽑는다. 활성 상태인 요소만 나온다.</summary>
    public static class UguiDumper
    {
        static readonly Vector3[] Corners = new Vector3[4];

        public static UiDump Dump() => Dump(Screen.width, Screen.height);

        /// <summary>화면 크기를 직접 넘긴다. Screen 값이 게임 화면과 다른 에디터 코드에서 쓴다.</summary>
        public static UiDump Dump(int screenWidth, int screenHeight)
        {
            var roots = new List<Canvas>();
            foreach (Canvas canvas in UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None))
                if (canvas.isRootCanvas && canvas.transform is RectTransform)
                    roots.Add(canvas);

            // ponytail: 루트 순서는 sortingOrder, 그다음 씬 루트의 형제 순서.
            // 씬 여러 개를 동시에 올려 쓰면 씬 순서도 비교에 넣어야 한다.
            roots.Sort((a, b) =>
            {
                int byOrder = a.sortingOrder.CompareTo(b.sortingOrder);
                return byOrder != 0
                    ? byOrder
                    : a.transform.root.GetSiblingIndex().CompareTo(b.transform.root.GetSiblingIndex());
            });

            var dump = new UiDump { screenWidth = screenWidth, screenHeight = screenHeight };
            foreach (Canvas canvas in roots)
                dump.roots.Add(Build((RectTransform)canvas.transform, CameraOf(canvas),
                    screenWidth, screenHeight, 1f, true, true));
            return dump;
        }

        static Camera CameraOf(Canvas canvas)
        {
            // Overlay, 그리고 카메라가 없는 ScreenSpaceCamera는 월드 좌표가 곧 화면 픽셀이다
            if (canvas.renderMode == RenderMode.ScreenSpaceOverlay) return null;
            if (canvas.worldCamera != null) return canvas.worldCamera;
            return canvas.renderMode == RenderMode.WorldSpace ? Camera.main : null;
        }

        static UiNode Build(RectTransform rt, Camera cam, int screenWidth, int screenHeight,
            float alpha, bool groupInteractable, bool drawn)
        {
            GameObject go = rt.gameObject;

            var canvas = go.GetComponent<Canvas>();
            if (canvas != null && !canvas.enabled) drawn = false;

            var group = go.GetComponent<CanvasGroup>();
            if (group != null && group.enabled)
            {
                if (group.ignoreParentGroups) { alpha = 1f; groupInteractable = true; }
                alpha *= group.alpha;
                groupInteractable &= group.interactable;
            }

            var selectable = go.GetComponent<Selectable>();
            var node = new UiNode { name = go.name, type = TypeOf(go, selectable), handle = go };
            node.text = TextOf(go, node.type);
            var qaplayId = go.GetComponent<QaplayId>();
            if (qaplayId != null && !string.IsNullOrEmpty(qaplayId.id)) node.id = qaplayId.id;

            rt.GetWorldCorners(Corners);
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            foreach (Vector3 corner in Corners)
            {
                Vector2 p = RectTransformUtility.WorldToScreenPoint(cam, corner);
                minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x);
                minY = Mathf.Min(minY, p.y); maxY = Mathf.Max(maxY, p.y);
            }
            // Unity 화면 좌표는 왼쪽 아래 원점, 프로토콜은 왼쪽 위 원점
            node.x = minX / screenWidth;
            node.y = 1f - maxY / screenHeight;
            node.w = (maxX - minX) / screenWidth;
            node.h = (maxY - minY) / screenHeight;

            // ponytail: RectMask2D/Mask로 잘린 요소(스크롤 영역 밖 아이템)도 visible로 나온다.
            // 스크롤 목록을 다루는 조각에서 마스크 영역과의 겹침 검사를 추가한다.
            node.visible = drawn && alpha > 0f
                && node.x < 1f && node.x + node.w > 0f
                && node.y < 1f && node.y + node.h > 0f;

            node.interactable = selectable != null
                ? selectable.enabled && selectable.IsInteractable()
                : groupInteractable && go.GetComponent<IPointerClickHandler>() != null;

            for (int i = 0; i < rt.childCount; i++)
                if (rt.GetChild(i) is RectTransform child && child.gameObject.activeSelf)
                    node.children.Add(Build(child, cam, screenWidth, screenHeight, alpha, groupInteractable, drawn));
            return node;
        }

        static string TypeOf(GameObject go, Selectable selectable)
        {
            switch (selectable)
            {
                case Button _: return "Button";
                case Toggle _: return "Toggle";
                case Slider _: return "Slider";
                case InputField _: return "InputField";
                case Dropdown _: return "Dropdown";
                case TMP_InputField _: return "InputField";
                case TMP_Dropdown _: return "Dropdown";
            }
            if (go.GetComponent<ScrollRect>() != null) return "ScrollView";
            if (go.GetComponent<Text>() != null || go.GetComponent<TMP_Text>() != null) return "Text";
            if (go.GetComponent<Image>() != null || go.GetComponent<RawImage>() != null) return "Image";
            return "Node";
        }

        static string TextOf(GameObject go, string type)
        {
            var input = go.GetComponent<InputField>();
            if (input != null) return input.text;
            var tmpInput = go.GetComponent<TMP_InputField>();
            if (tmpInput != null) return tmpInput.text;
            string own = TextIn(go.GetComponent<Graphic>());
            if (own != null) return own;
            if (type != "Button" && type != "Toggle" && type != "Dropdown") return null;

            // 글자가 없는 조작 요소는 자손의 글자를 문서 순서로 이어 붙인다 (명세 4절)
            var texts = new List<string>();
            foreach (Graphic graphic in go.GetComponentsInChildren<Graphic>())
            {
                string text = TextIn(graphic);
                if (text != null) texts.Add(text);
            }
            return texts.Count == 0 ? null : string.Join(" ", texts);
        }

        // 구형 Text와 TextMesh Pro는 둘 다 Graphic이다. 글자를 그리는 요소가 아니면 null.
        // ponytail: 서식 태그(<color=...> 등)가 들어 있으면 태그까지 그대로 나온다.
        // 화면에 보이는 글자로 찾아야 하면 TMP의 GetParsedText()를 쓰도록 바꾼다(그려진 뒤에만 값이 있다).
        static string TextIn(Graphic graphic)
        {
            switch (graphic)
            {
                case Text legacy: return legacy.text;
                case TMP_Text tmp: return tmp.text;
                default: return null;
            }
        }
    }
}
