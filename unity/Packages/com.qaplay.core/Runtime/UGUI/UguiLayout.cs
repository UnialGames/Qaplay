using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Qaplay.UGUI
{
    /// <summary>layout 명령. 지금 화면의 배치에서 문제를 찾는다: 글자 넘침, 요소 겹침. protocol/README.md의 layout.</summary>
    public static class UguiLayout
    {
        public static readonly string[] Checks = { "overflow", "overlap" };

        // 겹친 넓이가 작은 쪽 넓이의 이 비율이 안 되면 넘어간다. 맞닿은 테두리나 계산 오차를 겹침으로 보지 않는다
        const float MinOverlapShare = 0.01f;
        // TextMesh Pro의 글자 범위는 글꼴에 따라 영역을 조금 넘는다(기울임, 삐침). 글자 크기의 이 비율까지는 넘침으로 보지 않는다
        const float SlackPerFontSize = 0.15f;

        // 겹침을 볼 요소 하나: 누를 수 있는 요소, 또는 누를 수 있는 요소에 들어 있지 않은 글자
        sealed class Candidate
        {
            public UiNode node;
            public string path;
            public Transform transform;
            /// <summary>화면에서 차지하는 영역(정규화 좌표). 누를 수 있는 요소는 자기 칸, 글자는 실제로 그려진 범위이고 마스크에 잘린 만큼 줄어 있다.</summary>
            public Rect rect;
        }

        static readonly List<RaycastResult> Hits = new List<RaycastResult>();
        static readonly Vector3[] Corners = new Vector3[4];

        /// <summary>문제 목록(프로토콜의 issues 형식). checks가 null이면 모든 검사를 한다.</summary>
        public static List<object> Check(ICollection<string> checks = null)
        {
            // 방금 바뀐 글자와 배치가 반영된 뒤의 모습으로 판정한다
            Canvas.ForceUpdateCanvases();
            var issues = new List<object>();
            var candidates = new List<Candidate>();
            bool overflow = checks == null || checks.Contains("overflow"), overlap = checks == null || checks.Contains("overlap");

            UiDump dump = UguiDumper.Dump();
            foreach (UiNode root in dump.roots) Walk(root, root.name, false, overflow, overlap ? candidates : null, issues);
            if (overlap) Overlaps(candidates, dump, issues);
            return issues;
        }

        static void Walk(UiNode node, string path, bool insideInteractable, bool overflow, List<Candidate> candidates, List<object> issues)
        {
            var go = (GameObject)node.handle;
            if (node.visible)
            {
                if (overflow)
                {
                    string reason = Overflow(go);
                    if (reason != null)
                        issues.Add(new Dictionary<string, object>
                        {
                            ["kind"] = "overflow", ["reason"] = reason, ["path"] = path, ["element"] = node.ToData(false),
                        });
                }
                if (candidates != null)
                {
                    // 버튼 안의 글자는 버튼의 일부다. 따로 세면 버튼 둘의 겹침 하나가 여러 건으로 나온다
                    Rect rect = default;
                    bool take = node.interactable
                        ? Clip(new Rect(node.x, node.y, node.w, node.h), node, out rect)
                        : !insideInteractable && node.type == "Text" && DrawnText(go, out rect) && Clip(rect, node, out rect);
                    if (take) candidates.Add(new Candidate { node = node, path = path, transform = go.transform, rect = rect });
                }
            }
            insideInteractable |= node.interactable;
            foreach (UiNode child in node.children)
                Walk(child, path + "/" + child.name, insideInteractable, overflow, candidates, issues);
        }

        // ---- 글자 넘침 ----

        // 글자가 자기 영역에 다 들어가지 않았으면 그 모양을 돌려준다: truncated(잘림), spilled(삐져나옴). 문제가 없으면 null
        static string Overflow(GameObject go)
        {
            var tmp = go.GetComponent<TMP_Text>();
            if (tmp != null && tmp.enabled && !string.IsNullOrEmpty(tmp.text))
            {
                // 잘림: 말줄임표나 자르기 모드에서 들어가지 않은 글자를 버렸다
                if (tmp.isTextTruncated) return "truncated";
                // 삐져나옴: 그려진 글자의 범위가 영역을 벗어났다. 글꼴의 삐침만큼은 봐준다
                Bounds drawn = tmp.textBounds;
                Rect box = tmp.rectTransform.rect;
                float slack = Mathf.Max(1f, tmp.fontSize * SlackPerFontSize);
                if (drawn.size.x > 0f && (drawn.min.x < box.xMin - slack || drawn.max.x > box.xMax + slack
                                          || drawn.min.y < box.yMin - slack || drawn.max.y > box.yMax + slack))
                    return "spilled";
                return null;
            }

            var legacy = go.GetComponent<Text>();
            if (legacy != null && legacy.enabled && !string.IsNullOrEmpty(legacy.text))
            {
                Rect box = legacy.rectTransform.rect;
                // 삐져나옴: 넘치게 두는 모드에서 필요한 크기가 영역보다 크다
                if (legacy.horizontalOverflow == HorizontalWrapMode.Overflow && legacy.preferredWidth > box.width + 1f) return "spilled";
                if (legacy.verticalOverflow == VerticalWrapMode.Overflow && legacy.preferredHeight > box.height + 1f) return "spilled";
                // 잘림: 그려진 글자 수가 전체보다 적다(자르는 모드, 또는 맞춤 크기의 최소로도 들어가지 않음).
                // 서식 태그는 글자로 세지 않으므로 태그가 있으면 견줄 수 없다
                bool tagged = legacy.supportRichText && legacy.text.IndexOf('<') >= 0;
                if (!tagged && legacy.cachedTextGenerator.characterCountVisible < legacy.text.Length) return "truncated";
            }
            return null;
        }

        // ---- 요소 겹침 ----

        static void Overlaps(List<Candidate> candidates, UiDump dump, List<object> issues)
        {
            // 팝업이나 패널 뒤에 깔린 요소는 뺀다. 가려져 있는 것과 그 위의 것이 겹치는 것은 의도된 모습이다
            var owners = new HashSet<Transform>();
            foreach (Candidate candidate in candidates) owners.Add(candidate.transform);
            List<Candidate> shown = candidates.FindAll(c => !BehindSomething(c, owners, dump));

            for (int i = 0; i < shown.Count; i++)
            for (int j = i + 1; j < shown.Count; j++)
            {
                Candidate a = shown[i], b = shown[j];
                // 부모와 자손은 겹치는 것이 당연하다
                if (a.transform.IsChildOf(b.transform) || b.transform.IsChildOf(a.transform)) continue;
                float width = Mathf.Min(a.rect.xMax, b.rect.xMax) - Mathf.Max(a.rect.xMin, b.rect.xMin);
                float height = Mathf.Min(a.rect.yMax, b.rect.yMax) - Mathf.Max(a.rect.yMin, b.rect.yMin);
                if (width <= 0f || height <= 0f) continue;
                float share = width * height / Mathf.Min(a.rect.width * a.rect.height, b.rect.width * b.rect.height);
                if (share < MinOverlapShare) continue;
                issues.Add(new Dictionary<string, object>
                {
                    ["kind"] = "overlap", ["share"] = Math.Round(Mathf.Min(share, 1f), 2),
                    ["path"] = a.path, ["element"] = a.node.ToData(false),
                    ["otherPath"] = b.path, ["otherElement"] = b.node.ToData(false),
                });
            }
        }

        // 이 요소의 가운데 위에, 겹침을 보는 요소가 아닌 무언가(팝업의 바탕, 덮개)가 입력을 막고 있는지.
        // ponytail: 가운데 한 점만 본다. 절반만 덮인 요소는 가운데가 어디냐에 따라 갈린다.
        // 입력을 막지 않는(raycastTarget이 꺼진) 바탕은 덮개로 보지 못한다. Canvas가 여럿이면 sortingOrder로만 위아래를 가린다
        static bool BehindSomething(Candidate candidate, HashSet<Transform> owners, UiDump dump)
        {
            EventSystem eventSystem = EventSystem.current;
            if (eventSystem == null) return false;
            var pointer = new PointerEventData(eventSystem)
            {
                position = new Vector2(candidate.rect.center.x * dump.screenWidth, (1f - candidate.rect.center.y) * dump.screenHeight)
            };
            eventSystem.RaycastAll(pointer, Hits);

            int order = 0, depth = -1;
            foreach (Graphic graphic in candidate.transform.GetComponentsInChildren<Graphic>())
            {
                depth = Mathf.Max(depth, graphic.depth);
                if (graphic.canvas != null) order = graphic.canvas.rootCanvas.sortingOrder;
            }
            foreach (RaycastResult hit in Hits)
            {
                if (!(hit.module is GraphicRaycaster)) continue;
                Transform top = hit.gameObject.transform;
                // 자신과 자손, 그리고 조상(조상은 늘 뒤에 그려진다)은 덮개가 아니다
                if (top.IsChildOf(candidate.transform) || candidate.transform.IsChildOf(top)) continue;
                // 겹침을 보는 다른 요소가 위에 있는 것은 덮인 것이 아니라 겹친 것이다
                if (OwnedBy(top, owners)) continue;
                if (hit.sortingOrder > order || (hit.sortingOrder == order && hit.depth > depth)) return true;
            }
            return false;
        }

        static bool OwnedBy(Transform transform, HashSet<Transform> owners)
        {
            for (; transform != null; transform = transform.parent)
                if (owners.Contains(transform)) return true;
            return false;
        }

        // 마스크(스크롤 영역 등)에 잘린 뒤의 영역. 남는 것이 없으면 false
        static bool Clip(Rect rect, UiNode node, out Rect clipped)
        {
            clipped = Rect.MinMaxRect(
                Mathf.Max(rect.xMin, node.clipMinX), Mathf.Max(rect.yMin, node.clipMinY),
                Mathf.Min(rect.xMax, node.clipMaxX), Mathf.Min(rect.yMax, node.clipMaxY));
            return clipped.width > 0f && clipped.height > 0f;
        }

        // 글자가 실제로 그려진 범위(정규화 좌표, 왼쪽 위 원점). 칸이 넉넉해도 글자가 닿지 않으면 겹친 것이 아니고,
        // 칸 밖으로 삐져나온 글자는 그 자리에서 겹친다. 그려진 글자가 없으면 false
        static bool DrawnText(GameObject go, out Rect rect)
        {
            rect = default;
            Vector2 min, max;
            var graphic = go.GetComponent<Graphic>();
            if (graphic is TMP_Text tmp)
            {
                Bounds drawn = tmp.textBounds;
                if (drawn.size.x <= 0f || drawn.size.y <= 0f) return false;
                min = drawn.min; max = drawn.max;
            }
            else if (graphic is Text legacy)
            {
                IList<UIVertex> verts = legacy.cachedTextGenerator.verts;
                if (verts.Count == 0) return false;
                min = max = verts[0].position;
                foreach (UIVertex vert in verts)
                {
                    min = Vector2.Min(min, vert.position);
                    max = Vector2.Max(max, vert.position);
                }
                // 생성기의 좌표는 픽셀 단위다. 그릴 때처럼 요소의 단위로 바꾼다
                float unitsPerPixel = 1f / legacy.pixelsPerUnit;
                min *= unitsPerPixel; max *= unitsPerPixel;
                if (max.x <= min.x || max.y <= min.y) return false;
            }
            else return false;

            if (graphic.canvas == null) return false;
            Camera cam = UguiDumper.CameraOf(graphic.canvas.rootCanvas);
            Transform t = go.transform;
            Corners[0] = new Vector3(min.x, min.y); Corners[1] = new Vector3(min.x, max.y);
            Corners[2] = new Vector3(max.x, max.y); Corners[3] = new Vector3(max.x, min.y);
            float left = float.MaxValue, bottom = float.MaxValue, right = float.MinValue, top = float.MinValue;
            foreach (Vector3 corner in Corners)
            {
                Vector2 p = RectTransformUtility.WorldToScreenPoint(cam, t.TransformPoint(corner));
                left = Mathf.Min(left, p.x); right = Mathf.Max(right, p.x);
                bottom = Mathf.Min(bottom, p.y); top = Mathf.Max(top, p.y);
            }
            // Unity 화면 좌표는 왼쪽 아래 원점, 프로토콜은 왼쪽 위 원점
            rect = Rect.MinMaxRect(left / Screen.width, 1f - top / Screen.height, right / Screen.width, 1f - bottom / Screen.height);
            return true;
        }
    }
}
