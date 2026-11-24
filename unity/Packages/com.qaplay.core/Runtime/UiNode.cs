using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Qaplay
{
    /// <summary>화면의 UI 요소 하나. 필드의 뜻은 protocol/README.md 4절.</summary>
    public sealed class UiNode
    {
        public string name;
        public string type;
        /// <summary>요소가 보여 주는 글자. 없으면 null이고 JSON에서 빠진다.</summary>
        public string text;
        /// <summary>화면 영역. 왼쪽 위가 (0,0), 오른쪽 아래가 (1,1)인 정규화 좌표.</summary>
        public float x, y, w, h;
        public bool visible;
        public bool interactable;
        public readonly List<UiNode> children = new List<UiNode>();
        /// <summary>어댑터가 넣는 원본 객체(uGUI는 GameObject). JSON에는 나오지 않는다.</summary>
        public object handle;
    }

    /// <summary>dump 명령의 결과.</summary>
    public sealed class UiDump
    {
        public int screenWidth, screenHeight;
        public readonly List<UiNode> roots = new List<UiNode>();

        public string ToJson()
        {
            var sb = new StringBuilder();
            sb.Append("{\"screen\":{\"width\":").Append(screenWidth)
              .Append(",\"height\":").Append(screenHeight).Append("},\"roots\":");
            WriteNodes(sb, roots);
            return sb.Append('}').ToString();
        }

        static void WriteNodes(StringBuilder sb, List<UiNode> nodes)
        {
            sb.Append('[');
            for (int i = 0; i < nodes.Count; i++)
            {
                if (i > 0) sb.Append(',');
                UiNode n = nodes[i];
                sb.Append("{\"name\":"); WriteString(sb, n.name);
                sb.Append(",\"type\":"); WriteString(sb, n.type);
                if (n.text != null) { sb.Append(",\"text\":"); WriteString(sb, n.text); }
                sb.Append(",\"rect\":{\"x\":"); WriteNumber(sb, n.x);
                sb.Append(",\"y\":"); WriteNumber(sb, n.y);
                sb.Append(",\"w\":"); WriteNumber(sb, n.w);
                sb.Append(",\"h\":"); WriteNumber(sb, n.h);
                sb.Append("},\"visible\":").Append(n.visible ? "true" : "false");
                sb.Append(",\"interactable\":").Append(n.interactable ? "true" : "false");
                sb.Append(",\"children\":");
                WriteNodes(sb, n.children);
                sb.Append('}');
            }
            sb.Append(']');
        }

        static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // 소수 넷째 자리까지. 기기 로캘과 무관하게 소수점은 항상 '.'
        static void WriteNumber(StringBuilder sb, float v) =>
            sb.Append(Math.Round(v, 4).ToString("0.####", CultureInfo.InvariantCulture));
    }
}
