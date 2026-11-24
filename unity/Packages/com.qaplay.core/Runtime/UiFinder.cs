using System.Collections.Generic;

namespace Qaplay
{
    /// <summary>요소를 찾는 조건. 적은 조건을 모두 만족하는 요소가 걸린다. protocol/README.md 5절.</summary>
    public sealed class UiSelector
    {
        public string name;
        public string type;
        public string text;
        /// <summary>이 셀렉터에 걸리는 요소 하나의 자손만 대상으로 한다.</summary>
        public UiSelector within;
        /// <summary>다른 조건에 걸린 요소들 중 문서 순서로 몇 번째인지(0부터).</summary>
        public int? index;
    }

    /// <summary>검색에 걸린 요소. path는 사람이 위치를 알아보기 위한 표시용이다.</summary>
    public readonly struct UiMatch
    {
        public readonly UiNode node;
        public readonly string path;

        public UiMatch(UiNode node, string path)
        {
            this.node = node;
            this.path = path;
        }
    }

    public static class UiFinder
    {
        /// <summary>셀렉터에 걸리는 요소를 문서 순서로 모두 돌려준다. 0개여도 오류가 아니다.</summary>
        public static List<UiMatch> Find(UiDump dump, UiSelector s)
        {
            if (s == null || (s.name == null && s.type == null && s.text == null && s.within == null && s.index == null))
                throw new QaplayException("bad_request", "selector has no conditions");
            if (s.index < 0)
                throw new QaplayException("bad_request", "index must be 0 or greater");

            var matches = new List<UiMatch>();
            if (s.within == null)
            {
                foreach (UiNode root in dump.roots) Collect(root, null, s, matches);
            }
            else
            {
                // 범위 요소 자신은 대상이 아니고 그 자손만 본다
                UiMatch scope = FindOne(dump, s.within);
                foreach (UiNode child in scope.node.children) Collect(child, scope.path, s, matches);
            }

            if (s.index is int i)
                return i < matches.Count ? new List<UiMatch> { matches[i] } : new List<UiMatch>();
            return matches;
        }

        /// <summary>
        /// 요소 하나를 정한다. 없으면 not_found, 둘 이상이면 고르지 않고 ambiguous.
        /// 단, 걸린 요소들이 한 줄로 이어진 조상과 자손이면(버튼과 그 안의 글자 등) 가장 바깥 요소로 정한다.
        /// </summary>
        public static UiMatch FindOne(UiDump dump, UiSelector s)
        {
            List<UiMatch> matches = Find(dump, s);
            if (matches.Count == 0)
                throw new QaplayException("not_found", "no element matched the selector");
            // 문서 순서이므로, 각 요소가 바로 앞 요소의 자손이면 전체가 한 줄로 이어진 것이다
            for (int i = 1; i < matches.Count; i++)
                if (!Contains(matches[i - 1].node, matches[i].node))
                    throw new QaplayException("ambiguous", "selector matched " + matches.Count + " elements",
                        count: matches.Count);
            return matches[0];
        }

        static bool Contains(UiNode ancestor, UiNode node)
        {
            foreach (UiNode child in ancestor.children)
                if (child == node || Contains(child, node))
                    return true;
            return false;
        }

        static void Collect(UiNode n, string parentPath, UiSelector s, List<UiMatch> into)
        {
            string path = parentPath == null ? n.name : parentPath + "/" + n.name;
            if ((s.name == null || s.name == n.name)
                && (s.type == null || s.type == n.type)
                && (s.text == null || s.text == n.text))
                into.Add(new UiMatch(n, path));
            foreach (UiNode child in n.children) Collect(child, path, s, into);
        }
    }
}
