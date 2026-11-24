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

        /// <summary>
        /// 화면의 한 점을 눌렀다 뗀다. 좌표는 정규화(왼쪽 위 원점).
        /// 누름과 뗌을 서로 다른 프레임에 넣고, 뗌이 처리된 프레임 뒤에 끝난다.
        /// 마우스나 펜이 연결되어 있으면 한 프레임이 더 걸린다.
        /// </summary>
        public static IEnumerator Tap(float x, float y)
        {
            Touchscreen screen = Device();
            var position = new Vector2(x * Screen.width, (1f - y) * Screen.height);

            // 실제 터치스크린처럼 터치마다 새 ID를 쓴다. 같은 ID를 다시 쓰면 안 되는 이유:
            // 터치가 끝나도 장치에는 그 ID가 남고, 그 사이 마우스 입력이 들어오면 UI 입력 모듈이
            // 터치용 포인터 기록을 마우스용으로 바꿔 쓰면서 ID 목록은 고치지 않는다(Input System 1.14.2).
            // 그 뒤 같은 ID의 터치는 마우스 포인터로 분류되어 마우스 위치에서 눌린다.
            int touchId = ++s_LastTouchId;

            // 장치를 끄면 UI 입력 모듈의 액션이 취소되면서 마우스 포인터에 "이번 프레임에 바뀜" 표시가 남을 수 있다.
            // 그 표시가 남은 프레임에 터치가 들어오면 터치가 버려지므로, 끈 경우에는 한 프레임 보내고 터치를 넣는다.
            if (PauseMouseAndPen())
                yield return null;
            InputSystem.QueueStateEvent(screen,
                new TouchState { touchId = touchId, phase = TouchPhase.Began, position = position, pressure = 1f });
            yield return null;
            InputSystem.QueueStateEvent(screen,
                new TouchState { touchId = touchId, phase = TouchPhase.Ended, position = position });
            yield return null;
            ResumeMouseAndPen();
        }

        static int s_LastTouchId;
        static readonly List<InputDevice> s_Paused = new List<InputDevice>();

        // UI 입력 모듈은 기본 설정에서 마우스/펜과 터치가 같은 프레임에 들어오면 한쪽 포인터를 버린다.
        // 사람이 마우스를 움직이는 중에 넣은 터치가 버려지지 않도록, 탭하는 동안만 마우스와 펜을 끈다.
        // 탭이 중간에 끊겨 다시 켜지 못해도 다음 탭이 끝날 때 함께 켜진다.
        static bool PauseMouseAndPen()
        {
            foreach (InputDevice device in InputSystem.devices)
            {
                if (!(device is Mouse || device is Pen) || !device.enabled) continue;
                InputSystem.DisableDevice(device);
                s_Paused.Add(device);
            }
            return s_Paused.Count > 0;
        }

        static void ResumeMouseAndPen()
        {
            foreach (InputDevice device in s_Paused)
                if (device.added) InputSystem.EnableDevice(device);
            s_Paused.Clear();
        }

        static Touchscreen Device()
        {
            if (s_Screen != null && s_Screen.added) return s_Screen;
#if UNITY_EDITOR
            RouteInputToGameInEditor();
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
                ResumeMouseAndPen();
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
