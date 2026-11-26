using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using TouchPhase = UnityEngine.InputSystem.TouchPhase;

namespace Qaplay.Injection
{
    /// <summary>
    /// 가상 터치스크린에 터치를 넣는다. 버튼의 클릭 함수를 직접 부르지 않고,
    /// 실제 터치와 같은 경로(Input System → UI 입력 모듈)를 거치게 한다.
    /// </summary>
    public static class TouchInjector
    {
        static Touchscreen s_Screen;

        /// <summary>한 번에 넣을 수 있는 터치 수. 가상 터치스크린이 동시에 다루는 터치 수와 같다.</summary>
        public const int MaxPoints = 10;

        /// <summary>
        /// 화면의 한 점을 눌렀다 뗀다. 좌표는 정규화(왼쪽 위 원점).
        /// 네 프레임이 걸린다: 마우스와 펜을 막고 한 프레임, 누름, 뗌, 그리고 터치 기록이 지워지는 프레임.
        /// 그동안 마우스와 펜의 입력은 버려진다.
        /// </summary>
        public static IEnumerator Tap(float x, float y) => Tap(new[] { new Vector2(x, y) }, 1, 0);

        /// <summary>
        /// 여러 점을 같은 프레임에 누르고 다음 프레임에 함께 뗀다(동시 클릭). 그것을 count번 되풀이한다(연타).
        /// 되풀이할 때마다 두 프레임(누름, 뗌)이 더 걸린다. intervalMs는 뗀 뒤 다음 누름까지 더 기다리는 시간이다.
        /// 끝날 때까지 마우스와 펜의 입력은 버려진다.
        /// </summary>
        // ponytail: 클릭은 빨라야 두 프레임에 한 번이다. 한 프레임 안에 닫히는 틈은 연타로 잡지 못한다.
        // 필요해지면 뗌과 다음 누름을 같은 프레임에 넣는다.
        public static IEnumerator Tap(IReadOnlyList<Vector2> points, int count, int intervalMs)
        {
            Touchscreen screen = Device();
            var positions = new Vector2[points.Count];
            for (int i = 0; i < positions.Length; i++)
                positions[i] = new Vector2(points[i].x * Screen.width, (1f - points[i].y) * Screen.height);
            var touchIds = new int[positions.Length];

            MuteMouseAndPen();
            try
            {
                // 터치를 넣기 전에 한 프레임을 보낸다. UI 입력 모듈은 마우스 포인터에 "이번 프레임에 바뀜" 표시가 있는
                // 프레임에 터치가 들어오면 터치를 버린다. 그 표시는 모듈이 포인터를 처리해야 지워지는데, 처리하지 않고
                // 넘어가는 프레임이 있다(화면을 새로 만들어 EventSystem이 입력 모듈을 바꾸는 첫 프레임. 새 모듈은 그때
                // 마우스의 현재 위치를 한 번 받는다). 마우스를 막은 채로 한 프레임이 지나면 표시가 지워져 있다
                yield return null;
                for (int n = 0; n < count; n++)
                {
                    if (n > 0 && intervalMs > 0)
                    {
                        float until = Time.realtimeSinceStartup + intervalMs / 1000f;
                        while (Time.realtimeSinceStartup < until)
                        {
                            MuteMouseAndPen();
                            yield return null;
                        }
                    }
                    MuteMouseAndPen(); // 막는 기한을 이번 누름과 뗌만큼 늘린다

                    // 실제 터치스크린처럼 터치마다 새 ID를 쓴다. 같은 ID를 다시 쓰면 안 되는 이유:
                    // 터치가 끝나도 장치에는 그 ID가 남고, 그 사이 마우스 입력이 들어오면 UI 입력 모듈이
                    // 터치용 포인터 기록을 마우스용으로 바꿔 쓰면서 ID 목록은 고치지 않는다(Input System 1.14.2).
                    // 그 뒤 같은 ID의 터치는 마우스 포인터로 분류되어 마우스 위치에서 눌린다.
                    for (int i = 0; i < positions.Length; i++)
                    {
                        touchIds[i] = ++s_LastTouchId;
                        InputSystem.QueueStateEvent(screen, new TouchState
                            { touchId = touchIds[i], phase = TouchPhase.Began, position = positions[i], pressure = 1f });
                    }
                    yield return null;
                    for (int i = 0; i < positions.Length; i++)
                        InputSystem.QueueStateEvent(screen,
                            new TouchState { touchId = touchIds[i], phase = TouchPhase.Ended, position = positions[i] });
                    yield return null;
                }
                // 지금은 마지막 뗌이 처리된 프레임이다. UI 입력 모듈은 끝난 터치의 포인터 기록을 한 프레임 더 들고 있다가 지운다.
                // 그 기록이 지워진 뒤에 마우스와 펜을 다시 받는다(이유는 MuteMouseAndPen)
                yield return null;
                UnmuteMouseAndPen();
                RemindPointerPositions();
            }
            finally
            {
                UnmuteMouseAndPen();
            }
        }

        // UI 입력 모듈은 터치가 들어오면 마우스와 펜의 포인터 기록을 지운다. 그 뒤 마우스가 움직이지 않은 채 눌리면
        // 모듈은 위치를 모르는 채로 화면 왼쪽 아래 구석 (0, 0)에서 처리해서, 사람의 클릭이 커서 아래가 아니라
        // 구석에 있는 요소에 들어간다(Input System 1.14.2). 탭이 끝나면 위치를 한 번 흔들어 모듈이 지금 위치를 다시 받게 한다.
        // 모듈은 마우스와 펜을 포인터 하나로 다루고 마지막에 온 위치를 쓴다. 그래서 사람이 가장 최근에 쓴 장치를 맨 뒤에 보낸다
        // (에디터에는 한 번도 쓰지 않은 펜 장치가 (0, 0)에 있다. 그것이 뒤에 오면 다시 구석이 된다)
        static void RemindPointerPositions()
        {
            var pointers = new List<Pointer>();
            foreach (InputDevice device in InputSystem.devices)
                if ((device is Mouse || device is Pen) && device.enabled) pointers.Add((Pointer)device);
            pointers.Sort((a, b) => a.lastUpdateTime.CompareTo(b.lastUpdateTime));
            foreach (Pointer pointer in pointers)
            {
                Vector2 now = pointer.position.ReadValue();
                InputSystem.QueueDeltaStateEvent(pointer.position, now + Vector2.right);
                InputSystem.QueueDeltaStateEvent(pointer.position, now);
            }
        }

        static int s_LastTouchId;

        // 탭을 시작한 프레임부터 네 프레임(기다림, 누름, 뗌, 터치 기록이 지워지는 프레임) 동안 버린다.
        // 연타는 누를 때마다 이 기한을 다시 잡는다
        const int MuteFrames = 4;
        static int s_MutedUntilFrame;
        static bool s_Muted;

        // 탭하는 동안 마우스와 펜에서 오는 입력을 버린다. 사람이 쓰는 에디터나 PC 빌드에서 필요하다.
        // UI 입력 모듈(기본 설정)은 마우스·펜과 터치를 함께 다루지 못한다:
        // - 같은 프레임에 둘 다 들어오면 한쪽 포인터를 버린다. 마우스가 움직이면 넣은 터치가 버려진다.
        // - 터치의 포인터 기록이 남아 있는 동안 마우스 입력이 오면 마우스가 그 기록을 물려받는데, 위치는 터치의 위치
        //   그대로다. 그래서 마우스 버튼의 눌림이 방금 탭한 자리에서 처리되고, 뗄 때 그 요소가 한 번 더 클릭된다.
        //
        // 장치를 껐다 켜는 방법(InputSystem.DisableDevice)은 쓰지 않는다. 다시 켤 때 Input System이 장치의 현재 상태를
        // 다시 받아 오므로, 사람이 버튼을 누르고 있었다면 "눌림"이 새로 들어와 위의 두 번째 문제를 그대로 일으킨다.
        // ponytail: 버려진 입력은 되살리지 않는다. 탭하는 세 프레임 사이에 누르거나 뗀 마우스 버튼은 게임이 보지 못한다.
        static void MuteMouseAndPen()
        {
            s_MutedUntilFrame = Time.frameCount + MuteFrames;
            if (s_Muted) return;
            s_Muted = true;
            InputSystem.onEvent += DropMouseAndPen;
        }

        static void UnmuteMouseAndPen()
        {
            if (!s_Muted) return;
            s_Muted = false;
            InputSystem.onEvent -= DropMouseAndPen;
        }

        static void DropMouseAndPen(InputEventPtr eventPtr, InputDevice device)
        {
            // 탭이 중간에 끊겨(코루틴을 돌리던 쪽이 사라짐) 되돌리지 못했어도, 마우스가 계속 죽어 있지 않게 스스로 끝낸다
            if (Time.frameCount > s_MutedUntilFrame)
            {
                UnmuteMouseAndPen();
                return;
            }
            if (device is Mouse || device is Pen) eventPtr.handled = true;
        }

        static Touchscreen Device()
        {
            if (s_Screen != null && s_Screen.added) return s_Screen;
#if UNITY_EDITOR
            RouteInputToGameInEditor();
#else
            // PC 빌드: 창에 포커스가 없어도(러너가 터미널에서 도는 동안) 게임이 계속 돌고 주입한 터치를 받게 한다.
            // 장치를 추가하기 전에 불러야 한다
            Application.runInBackground = true;
            InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
#endif
            s_Screen = InputSystem.AddDevice<Touchscreen>("qaplay Touchscreen");
            return s_Screen;
        }

#if UNITY_EDITOR
        static bool s_Routed;

        // 에디터에서는 Game 뷰에 포커스가 없으면 포인터 입력이 게임에 가지 않고,
        // 그 상태에서 추가한 장치는 비활성화된다(Input System 기본 설정).
        // 주입한 터치가 포커스와 무관하게 게임에 가도록 Play 동안만 설정을 바꾸고, Play가 끝나면 되돌린다.
        // 장치를 추가하기 전에 불러야 한다.
        static void RouteInputToGameInEditor()
        {
            if (s_Routed) return;
            s_Routed = true;

            InputSettings settings = InputSystem.settings;
            var background = settings.backgroundBehavior;
            var editorBehavior = settings.editorInputBehaviorInPlayMode;
            bool runInBackground = Application.runInBackground;

            settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
            settings.editorInputBehaviorInPlayMode =
                InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
            Application.runInBackground = true;

            void Restore(UnityEditor.PlayModeStateChange change)
            {
                if (change != UnityEditor.PlayModeStateChange.ExitingPlayMode) return;
                UnityEditor.EditorApplication.playModeStateChanged -= Restore;
                UnmuteMouseAndPen();
                if (s_Screen != null && s_Screen.added) InputSystem.RemoveDevice(s_Screen);
                s_Screen = null;
                settings.backgroundBehavior = background;
                settings.editorInputBehaviorInPlayMode = editorBehavior;
                Application.runInBackground = runInBackground;
                s_Routed = false;
            }
            UnityEditor.EditorApplication.playModeStateChanged += Restore;
        }
#endif
    }
}
