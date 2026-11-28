using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Qaplay
{
    /// <summary>
    /// UI 시스템 하나(uGUI, UI Toolkit)를 다루는 어댑터(전략). 에이전트와 규칙 실행은 UI 시스템을 모르고 이것만 본다.
    /// 어댑터는 자기 어셈블리가 읽힐 때 UiAdapters.Register로 스스로 등록한다(코어는 어댑터의 어셈블리를 참조하지 않는다).
    /// 새 UI 시스템은 이것을 하나 더 만들면 된다. 한 프로젝트에 둘 다 있으면 둘 다 쓴다.
    /// </summary>
    public interface IUiAdapter
    {
        /// <summary>hello의 ui에 실리는 이름: ugui, uitk.</summary>
        string Name { get; }

        /// <summary>그 UI 시스템의 맨 위 요소들을 dump.roots에 더한다. 좌표는 화면에 대한 비율이고 왼쪽 위가 원점이다.</summary>
        void Dump(UiDump dump);

        /// <summary>
        /// 이 어댑터가 dump에 넣은 node를 point(화면에 대한 비율)에서 누르면 그 node에 터치가 닿는지. 닿지 않으면 not_tappable로 던진다.
        /// 화면 안인지, 보이는지, 입력을 받는지는 부르는 쪽이 먼저 본다. 여기서는 가려졌는지 같은 그 UI 시스템만 아는 것을 본다.
        /// </summary>
        void CheckTappable(UiDump dump, UiNode node, Vector2 point);

        /// <summary>화면 배치 문제(글자 넘침, 요소 겹침). checks는 UiSystem.LayoutChecks 가운데 몇 개, null이면 전부.</summary>
        List<object> Layout(ICollection<string> checks);

        /// <summary>게임이 시작될 때 한 번, 씬을 읽기 전에 부른다(이미지 잔상 감시를 건다).</summary>
        void Start();

        /// <summary>매 프레임 부른다.</summary>
        void Tick();

        /// <summary>지금까지 모은 이미지 잔상 기록을 넘기고 비운다.</summary>
        List<object> TakeGhosts();

        /// <summary>사람의 조작을 듣기 시작한다(버그 제보에 붙일 재현 스텝).</summary>
        void ListenHistory();

        /// <summary>들은 조작을 규칙 파일의 줄로.</summary>
        List<string> History();
    }

    /// <summary>등록된 UI 어댑터들. 어댑터의 어셈블리가 읽힐 때(SubsystemRegistration) 스스로 넣는다.</summary>
    public static class UiAdapters
    {
        static readonly List<IUiAdapter> s_All = new List<IUiAdapter>();

        /// <summary>같은 이름의 어댑터가 이미 있으면 바꾼다(에디터에서 Play를 다시 시작할 때).</summary>
        public static void Register(IUiAdapter adapter)
        {
            s_All.RemoveAll(a => a.Name == adapter.Name);
            s_All.Add(adapter);
        }

        public static IReadOnlyList<IUiAdapter> All => s_All;
    }

    /// <summary>
    /// 에이전트가 보는 UI: 켜진 어댑터들을 하나로 묶는다. 요소 트리는 모든 어댑터의 것을 합치고, 누를 수 있는지는 그 요소를 낸 어댑터에게 묻는다.
    /// 에이전트를 만드는 쪽이 만들어 넘긴다.
    /// </summary>
    public sealed class UiSystem
    {
        /// <summary>규칙의 assertLayout과 layout 명령이 받는 검사.</summary>
        public static readonly string[] LayoutChecks = { "overflow", "overlap" };

        readonly List<IUiAdapter> m_Adapters;

        public UiSystem(IEnumerable<IUiAdapter> adapters)
        {
            m_Adapters = new List<IUiAdapter>(adapters);
        }

        /// <summary>등록된 어댑터 전부로 만든 것.</summary>
        public static UiSystem Registered => new UiSystem(UiAdapters.All);

        public IReadOnlyList<IUiAdapter> Adapters => m_Adapters;

        public string[] Names => m_Adapters.ConvertAll(a => a.Name).ToArray();

        public UiDump Dump() => Dump(Screen.width, Screen.height);

        public UiDump Dump(int screenWidth, int screenHeight)
        {
            var dump = new UiDump { screenWidth = screenWidth, screenHeight = screenHeight };
            foreach (IUiAdapter adapter in m_Adapters)
            {
                int first = dump.roots.Count;
                adapter.Dump(dump);
                for (int i = first; i < dump.roots.Count; i++) Own(dump.roots[i], adapter);
            }
            return dump;
        }

        // 요소마다 그것을 낸 어댑터를 적어 둔다: 누를 수 있는지를 그 어댑터에게 묻는다
        static void Own(UiNode node, IUiAdapter adapter)
        {
            node.owner = adapter;
            foreach (UiNode child in node.children) Own(child, adapter);
        }

        public Vector2 PointOf(UiDump dump, UiSelector selector) =>
            UiTap.PointOf(dump, selector, (d, node, point) =>
            {
                if (node.owner == null) throw new QaplayException("internal", node.name + " came from no UI adapter");
                node.owner.CheckTappable(d, node, point);
            });

        public IEnumerator WaitForPoints(IReadOnlyList<UiSelector> selectors, int timeoutMs, Action<Vector2[]> onPoints) =>
            UiTap.WaitForPoints(Dump, PointOf, selectors, timeoutMs, onPoints);

        public IEnumerator Until(UiSelector selector, string state, int timeoutMs) => UiWait.Until(Dump, selector, state, timeoutMs);

        public List<object> Layout(ICollection<string> checks)
        {
            var issues = new List<object>();
            foreach (IUiAdapter adapter in m_Adapters) issues.AddRange(adapter.Layout(checks));
            return issues;
        }

        public List<object> TakeGhosts()
        {
            var images = new List<object>();
            foreach (IUiAdapter adapter in m_Adapters) images.AddRange(adapter.TakeGhosts());
            return images;
        }

        public void ListenHistory()
        {
            foreach (IUiAdapter adapter in m_Adapters) adapter.ListenHistory();
        }

        public List<string> History()
        {
            var lines = new List<string>();
            foreach (IUiAdapter adapter in m_Adapters) lines.AddRange(adapter.History());
            return lines;
        }
    }

    /// <summary>누를 자리 정하기와 누를 수 있을 때까지 기다리기. UI 시스템과 상관없는 부분이고, 가려졌는지는 받은 검사(checkHit)가 본다.</summary>
    public static class UiTap
    {
        /// <summary>
        /// 요소 하나의 중심점을 정규화 좌표(왼쪽 위 원점)로 돌려준다.
        /// 누를 수 없으면 터치를 넣기 전에 not_tappable로 알린다. 이유는 예외의 reason에 담긴다.
        /// </summary>
        public static Vector2 PointOf(UiDump dump, UiSelector selector, Action<UiDump, UiNode, Vector2> checkHit)
        {
            UiNode node = UiFinder.FindOne(dump, selector).node;
            var point = new Vector2(node.x + node.w / 2f, node.y + node.h / 2f);
            if (point.x < 0f || point.x > 1f || point.y < 0f || point.y > 1f)
                throw NotTappable("off_screen", node.name + " center is outside the screen");
            if (!node.visible)
                throw NotTappable("not_visible", node.name + " is not visible");
            if (!node.interactable)
                throw NotTappable("not_interactable", node.name + " does not accept input");
            checkHit(dump, node, point);
            return point;
        }

        public static QaplayException NotTappable(string reason, string message) => new QaplayException("not_tappable", message, reason);

        /// <summary>
        /// 누르기 전 자동 대기. 대상이 모두 나타나고 누를 수 있게 될 때까지 매 프레임 다시 검사해, 되는 순간의 위치들을 onPoints로 넘긴다.
        /// 위치는 모두 같은 프레임의 화면에서 잰 것이다.
        /// 제한 시간이 지나면 마지막 검사의 오류(not_found, not_tappable)를 그대로 던진다. 0이면 한 번만 검사한다.
        /// 여러 요소가 걸리는 것(ambiguous)은 기다려도 달라지지 않는 셀렉터 문제로 보고 바로 던진다.
        /// </summary>
        public static IEnumerator WaitForPoints(Func<UiDump> dumpNow, Func<UiDump, UiSelector, Vector2> pointOf,
            IReadOnlyList<UiSelector> selectors, int timeoutMs, Action<Vector2[]> onPoints)
        {
            if (timeoutMs < 0)
                throw new QaplayException("bad_request", "timeoutMs must be 0 or greater");

            float start = Time.realtimeSinceStartup;
            var points = new Vector2[selectors.Count];
            while (true)
            {
                try
                {
                    UiDump dump = dumpNow();
                    for (int i = 0; i < points.Length; i++) points[i] = pointOf(dump, selectors[i]);
                    onPoints(points);
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
    }
}
