using System.Text;
using Qaplay.UGUI;
using UnityEditor;
using UnityEngine;

namespace Qaplay.Editor
{
    /// <summary>현재 화면의 UI 요소를 Console에 찍는다. 테스트를 쓸 때 요소를 찾는 용도.</summary>
    static class DumpMenu
    {
        [MenuItem("qaplay/Dump UI")]
        static void DumpUi()
        {
            // 메뉴 콜백에서는 Screen 크기가 게임 화면과 다를 수 있어 Game 뷰 크기를 직접 넘긴다
            Vector2 size = Handles.GetMainGameViewSize();
            UiDump dump = UguiDumper.Dump((int)size.x, (int)size.y);

            var sb = new StringBuilder();
            sb.Append("[qaplay] UI dump ").Append(dump.screenWidth).Append('x').Append(dump.screenHeight).Append('\n');
            foreach (UiNode root in dump.roots) AppendTree(sb, root, 0);
            Debug.Log(sb.ToString());
            Debug.Log("[qaplay] dump json: " + dump.ToJson());
        }

        static void AppendTree(StringBuilder sb, UiNode n, int depth)
        {
            sb.Append(' ', depth * 2).Append(n.type).Append(' ').Append(n.name);
            if (n.id != null) sb.Append(" id=\"").Append(n.id).Append('"');
            if (n.text != null) sb.Append(" text=\"").Append(n.text).Append('"');
            sb.AppendFormat(System.Globalization.CultureInfo.InvariantCulture,
                " rect=({0:0.####}, {1:0.####}, {2:0.####}, {3:0.####})", n.x, n.y, n.w, n.h);
            sb.Append(" visible=").Append(n.visible ? "true" : "false");
            sb.Append(" interactable=").Append(n.interactable ? "true" : "false").Append('\n');
            foreach (UiNode child in n.children) AppendTree(sb, child, depth + 1);
        }
    }
}
