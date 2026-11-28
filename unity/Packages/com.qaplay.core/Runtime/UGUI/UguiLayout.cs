using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Qaplay.UGUI
{
    /// <summary>layout 명령. 지금 화면의 배치에서 문제를 찾는다: 글자 넘침. protocol/README.md의 layout.</summary>
    public static class UguiLayout
    {
        public static readonly string[] Checks = { "overflow" };

        /// <summary>문제 목록(프로토콜의 issues 형식). checks가 null이면 모든 검사를 한다.</summary>
        public static List<object> Check(ICollection<string> checks = null)
        {
            // 방금 바뀐 글자와 배치가 반영된 뒤의 모습으로 판정한다
            Canvas.ForceUpdateCanvases();
            var issues = new List<object>();
            UiDump dump = UguiDumper.Dump();
            foreach (UiNode root in dump.roots) Walk(root, root.name, checks, issues);
            return issues;
        }

        static void Walk(UiNode node, string path, ICollection<string> checks, List<object> issues)
        {
            if (node.visible && (checks == null || checks.Contains("overflow")))
            {
                string reason = Overflow((GameObject)node.handle);
                if (reason != null)
                    issues.Add(new Dictionary<string, object>
                    {
                        ["kind"] = "overflow", ["reason"] = reason, ["path"] = path, ["element"] = node.ToData(false),
                    });
            }
            foreach (UiNode child in node.children) Walk(child, path + "/" + child.name, checks, issues);
        }

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

        // TextMesh Pro의 글자 범위는 글꼴에 따라 영역을 조금 넘는다(기울임, 삐침). 글자 크기의 이 비율까지는 넘침으로 보지 않는다
        const float SlackPerFontSize = 0.15f;
    }
}
