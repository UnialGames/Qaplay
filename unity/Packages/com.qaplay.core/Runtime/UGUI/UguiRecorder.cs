using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.UI;

namespace Qaplay.UGUI
{
    /// <summary>
    /// 입력 기록. 사람이 누른 요소를 셀렉터로 바꿔 규칙 파일의 줄로 쌓는다. docs/rules.md 7절.
    /// UI 입력 모듈이 쓰는 누르기 액션을 함께 들으므로 마우스, 터치, 펜을 모두 같은 길로 받는다.
    /// </summary>
    // ponytail: 탭만 기록한다(끌기, 길게 누르기, 글자 입력은 규칙 명령이 생길 때 추가).
    // 한 번에 포인터 하나만 따라간다. 두 손가락을 동시에 누르면 먼저 누른 쪽만 기록된다.
    public static class UguiRecorder
    {
        /// <summary>기록된 줄. 명령이거나, 기록하지 못한 터치를 알리는 주석이다.</summary>
        public static readonly List<string> Lines = new List<string>();
        /// <summary>줄이 늘거나 기록 상태가 바뀌면 불린다.</summary>
        public static event Action Changed;
        public static bool IsRecording { get; private set; }

        static readonly List<RaycastResult> Hits = new List<RaycastResult>();
        static InputAction s_Click;
        static InputControl s_Down;   // 지금 눌려 있는 포인터
        static Vector2 s_DownAt;
        static string s_Pending;      // 누른 순간에 정해 둔 줄. 떼었을 때 탭으로 확인되면 기록한다

        // 최근 조작 이력: 제보에 붙인다. 기록(Start~Stop)과 무관하게 듣는 동안 늘 쌓인다
        const float HistorySeconds = 300f;
        const int HistoryMax = 200;
        static readonly List<(float time, string line)> s_History = new List<(float, string)>();
        static bool s_Listening;

        /// <summary>
        /// 입력을 듣기 시작한다. 한 번 켜면 Play가 끝날 때까지 듣는다.
        /// 듣는 동안의 조작은 History로 남고, Start와 Stop 사이의 것은 Lines에도 쌓인다.
        /// </summary>
        public static void Listen()
        {
            if (s_Listening) return;
            s_Listening = true;
            InputSystem.onAfterUpdate += Follow;
            Application.quitting += Unlisten;
            Follow();
        }

        static void Unlisten()
        {
            s_Listening = false;
            IsRecording = false;
            InputSystem.onAfterUpdate -= Follow;
            Application.quitting -= Unlisten;
            Bind(null);
            s_History.Clear();
        }

        /// <summary>앞서 기록한 줄을 지우고 새로 기록을 시작한다.</summary>
        public static void Start()
        {
            Listen();
            if (IsRecording) return;
            Lines.Clear();
            IsRecording = true;
            Changed?.Invoke();
        }

        public static void Stop()
        {
            if (!IsRecording) return;
            IsRecording = false;
            Changed?.Invoke();
        }

        /// <summary>최근 조작(5분 이내, 최대 200개)을 오래된 것부터 돌려준다.</summary>
        public static List<string> History()
        {
            s_History.RemoveAll(entry => Time.realtimeSinceStartup - entry.time > HistorySeconds);
            return s_History.ConvertAll(entry => entry.line);
        }

        static void Record(string line)
        {
            s_History.Add((Time.realtimeSinceStartup, line));
            if (s_History.Count > HistoryMax) s_History.RemoveAt(0);
            if (!IsRecording) return;
            Lines.Add(line);
            Changed?.Invoke();
        }

        /// <summary>기록한 줄을 규칙 파일의 내용으로 돌려준다.</summary>
        public static string ToYaml() => string.Join("\n", Lines) + "\n";

        // 씬이 바뀌면 EventSystem과 입력 모듈이 새로 생긴다. 지금 쓰이는 모듈의 누르기 액션을 따라간다
        static void Follow()
        {
            EventSystem eventSystem = EventSystem.current;
            var module = eventSystem != null ? eventSystem.GetComponent<InputSystemUIInputModule>() : null;
            Bind(module != null && module.leftClick != null ? module.leftClick.action : null);
        }

        static void Bind(InputAction click)
        {
            if (click == s_Click) return;
            if (s_Click != null)
            {
                s_Click.performed -= OnClick;
                s_Click.canceled -= OnClick;
            }
            s_Click = click;
            s_Down = null;
            if (s_Click != null)
            {
                s_Click.performed += OnClick;
                s_Click.canceled += OnClick;
            }
        }

        // 누름과 뗌이 모두 이리로 온다(액션 종류에 따라 뗌이 performed로도 canceled로도 온다). 버튼의 상태로 구분한다.
        // 이 시점은 UI 입력 모듈이 이번 입력을 처리하기 전이라, 화면은 아직 누르기 전 상태다
        static void OnClick(InputAction.CallbackContext context)
        {
            InputControl control = context.control;
            if (!(control is ButtonControl button)) return;
            // 장치가 뽑히면 액션이 취소되면서 이리로 온다. 사라진 장치의 값은 읽을 수 없다
            if (!control.device.added)
            {
                if (control == s_Down) s_Down = null;
                return;
            }

            if (button.isPressed)
            {
                if (s_Down != null) return; // 눌린 채 값만 바뀜(필압 등)
                s_Down = control;
                s_DownAt = PositionOf(control);
                s_Pending = LineFor(s_DownAt);
            }
            else if (control == s_Down)
            {
                s_Down = null;
                EventSystem eventSystem = EventSystem.current;
                float threshold = eventSystem != null ? eventSystem.pixelDragThreshold : 10f;
                // 누른 자리에서 뗀 것만 탭이다. 멀리 끌고 가서 떼면 스크롤 같은 다른 조작이다
                if (s_Pending == null || Vector2.Distance(PositionOf(control), s_DownAt) > threshold) return;
                Record(s_Pending);
            }
        }

        // 화면 픽셀 좌표(왼쪽 아래 원점). 터치는 그 손가락의 위치, 마우스와 펜은 장치의 위치
        static Vector2 PositionOf(InputControl press)
        {
            if (press.parent is TouchControl touch) return touch.position.ReadValue();
            return press.device is Pointer pointer ? pointer.position.ReadValue() : Vector2.zero;
        }

        // 그 자리를 누르면 입력을 받을 요소를 찾아 tapOn 한 줄로 적는다. 화면 밖이면 null(기록하지 않음)
        static string LineFor(Vector2 position)
        {
            EventSystem eventSystem = EventSystem.current;
            if (eventSystem == null || position.x < 0f || position.y < 0f
                || position.x > Screen.width || position.y > Screen.height)
                return null;

            // UI 입력 모듈과 같은 방법: 맨 위에 걸린 요소에서 위로 올라가며 클릭을 받는 요소를 찾는다.
            // 버튼 안의 글자를 눌렀으면 버튼이 나온다
            eventSystem.RaycastAll(new PointerEventData(eventSystem) { position = position }, Hits);
            GameObject target = Hits.Count == 0 ? null : ExecuteEvents.GetEventHandler<IPointerClickHandler>(Hits[0].gameObject);
            UiDump dump = UguiDumper.Dump();
            UiNode node = target == null ? null : NodeOf(dump.roots, target);
            if (node == null)
                return string.Format(CultureInfo.InvariantCulture, "# not recorded: no element takes a tap at ({0:0.###}, {1:0.###})",
                    position.x / Screen.width, 1f - position.y / Screen.height);
            return RuleRecorder.TapLine(RuleRecorder.SelectorFor(dump, node));
        }

        static UiNode NodeOf(List<UiNode> nodes, GameObject target)
        {
            foreach (UiNode node in nodes)
            {
                if (ReferenceEquals(node.handle, target)) return node;
                UiNode found = NodeOf(node.children, target);
                if (found != null) return found;
            }
            return null;
        }
    }
}
