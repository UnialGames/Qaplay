using System;
using System.Collections.Generic;

namespace Qaplay
{
    /// <summary>
    /// 입력 기록의 화면과 무관한 부분: 누른 요소를 셀렉터로 바꾸고 규칙 파일의 한 줄로 적는다. docs/rules.md 7절.
    /// 실제 입력을 듣는 쪽은 어댑터(uGUI는 UguiRecorder)다.
    /// </summary>
    public static class RuleRecorder
    {
        /// <summary>
        /// 화면에서 target 하나로 정해지는 셀렉터를 정한다.
        /// 순서: 고유 ID, 이름, 글자, 범위(within)를 덧붙인 이름과 글자, 마지막으로 순서(index).
        /// </summary>
        public static UiSelector SelectorFor(UiDump dump, UiNode target)
        {
            var ancestors = new List<UiNode>(); // 가까운 조상부터
            if (!Trail(dump.roots, target, ancestors)) throw new ArgumentException("target is not in the dump");

            foreach (UiSelector s in Candidates(target, null, true))
                if (Picks(dump, s, target)) return s;

            // 범위는 고유 ID나 이름만으로 하나로 정해지는 가장 가까운 조상이다
            UiSelector scope = null;
            foreach (UiNode ancestor in ancestors)
            {
                foreach (UiSelector s in Candidates(ancestor, null, false))
                    if (scope == null && Picks(dump, s, ancestor)) scope = s;
                if (scope != null) break;
            }
            if (scope != null)
                foreach (UiSelector s in Candidates(target, scope, true))
                    if (s.id == null && Picks(dump, s, target)) return s;

            // 그래도 여럿이면 같은 이름 중 몇 번째인지 적는다. 범위로 쓸 조상이 없으면 화면 전체에서 센다
            var byName = new UiSelector { name = target.name, within = scope };
            byName.index = UiFinder.Find(dump, byName).FindIndex(m => m.node == target);
            return byName;
        }

        static IEnumerable<UiSelector> Candidates(UiNode node, UiSelector within, bool withText)
        {
            if (node.id != null) yield return new UiSelector { id = node.id, within = within };
            yield return new UiSelector { name = node.name, within = within };
            if (withText && node.text != null) yield return new UiSelector { text = node.text, within = within };
        }

        // 재생할 때 tap이 고르는 요소가 바로 그 요소인지. 판정은 에이전트와 같은 코드(UiFinder)가 한다
        static bool Picks(UiDump dump, UiSelector selector, UiNode node)
        {
            List<UiMatch> matches = UiFinder.Find(dump, selector);
            return UiFinder.Resolves(matches) == "one" && matches[0].node == node;
        }

        // target의 조상을 가까운 것부터 into에 담는다. 화면에 없으면 false
        static bool Trail(List<UiNode> nodes, UiNode target, List<UiNode> into)
        {
            foreach (UiNode node in nodes)
            {
                if (node == target) return true;
                if (!Trail(node.children, target, into)) continue;
                into.Add(node);
                return true;
            }
            return false;
        }

        /// <summary>셀렉터를 규칙 파일의 tapOn 한 줄로 적는다. RuleReader가 읽으면 같은 셀렉터가 된다.</summary>
        public static string TapLine(UiSelector selector) => "- tapOn: " + Write(selector);

        static string Write(UiSelector s)
        {
            var parts = new List<string>();
            if (s.id != null) parts.Add("id: " + Scalar(s.id));
            if (s.name != null) parts.Add("name: " + Scalar(s.name));
            if (s.type != null) parts.Add("type: " + Scalar(s.type));
            if (s.text != null) parts.Add("text: " + Scalar(s.text));
            if (s.within != null) parts.Add("within: " + Write(s.within));
            if (s.index != null) parts.Add("index: " + s.index);
            return "{ " + string.Join(", ", parts) + " }";
        }

        // 따옴표 없이 적어도 틀림없이 같은 글자로 읽히는 것만 그대로 적고, 나머지는 큰따옴표로 감싼다.
        // 큰따옴표 안의 이스케이프는 JSON과 같아서 줄바꿈이 든 글자도 한 줄에 적힌다
        static string Scalar(string text)
        {
            bool plain = text.Length > 0 && char.IsLetter(text[0]) && text[text.Length - 1] != ' '
                         && text != "true" && text != "false" && text != "null";
            foreach (char c in text)
                plain &= char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.' || c == ' ';
            return plain ? text : Json.Write(text);
        }
    }
}
