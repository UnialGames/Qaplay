using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Qaplay.UGUI;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Qaplay.Agent
{
    /// <summary>
    /// 버그 제보. 테스터가 단축키(F12)나 제스처(세 손가락으로 1초 누르기)로 제보하면, 그 순간의 화면과
    /// 최근 조작 이력, 로그, 기기 정보를 기기 안의 폴더에 저장한다. 러너가 나중에 가져간다(report 명령들).
    /// 제목과 설명은 기기에서 받지 않는다. 가져간 뒤 PC에서 적는다.
    /// </summary>
    public sealed class BugReporter : MonoBehaviour
    {
        const int LogLines = 200;
        const float HoldSeconds = 1f;

        static readonly Queue<string> s_Log = new Queue<string>();
        static BugReporter s_Instance;

        float m_HeldSince = -1f;
        bool m_Fired;
        float m_ToastUntil;
        string m_Toast;

        /// <summary>제보가 쌓이는 폴더. 제보 하나가 하위 폴더 하나다.</summary>
        public static string Folder => Path.Combine(Application.persistentDataPath, "qaplay-reports");

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AutoStart()
        {
            if (s_Instance != null) return;
            var go = new GameObject("qaplay reporter") { hideFlags = HideFlags.HideInHierarchy };
            DontDestroyOnLoad(go);
            s_Instance = go.AddComponent<BugReporter>();
            Application.logMessageReceivedThreaded += OnLog;
            UguiRecorder.Listen(); // 제보에 붙일 조작 이력을 지금부터 쌓는다
        }

        void OnDestroy()
        {
            Application.logMessageReceivedThreaded -= OnLog;
            if (s_Instance == this) s_Instance = null;
        }

        static void OnLog(string message, string stackTrace, LogType type)
        {
            string line = "[" + type + "] " + message;
            // 스택은 문제가 된 로그에만 붙인다
            if (type != LogType.Log && type != LogType.Warning && !string.IsNullOrEmpty(stackTrace))
                line += "\n" + stackTrace.TrimEnd();
            lock (s_Log)
            {
                s_Log.Enqueue(line);
                while (s_Log.Count > LogLines) s_Log.Dequeue();
            }
        }

        // ---- 사람이 거는 제보 ----

        void Update()
        {
            bool key = Keyboard.current != null && Keyboard.current.f12Key.wasPressedThisFrame;

            int fingers = 0;
            foreach (InputDevice device in InputSystem.devices)
                if (device is Touchscreen screen)
                    foreach (var touch in screen.touches)
                        if (touch.press.isPressed) fingers++;
            if (fingers < 3)
            {
                m_HeldSince = -1f;
                m_Fired = false;
            }
            else if (m_HeldSince < 0f) m_HeldSince = Time.realtimeSinceStartup;
            bool gesture = !m_Fired && m_HeldSince >= 0f && Time.realtimeSinceStartup - m_HeldSince >= HoldSeconds;

            if (!key && !gesture) return;
            m_Fired = true; // 손을 뗄 때까지 한 번만
            StartCoroutine(Create(null, null, id =>
            {
                m_Toast = "qaplay: report saved (" + id + ")";
                m_ToastUntil = Time.realtimeSinceStartup + 2f;
            }));
        }

        // 저장했다는 표시. uGUI가 아니라서 게임 UI의 요소로 잡히지 않고 터치도 받지 않는다
        void OnGUI()
        {
            if (Time.realtimeSinceStartup > m_ToastUntil) return;
            GUI.Label(new Rect(10f, 10f, Screen.width - 20f, 30f), m_Toast);
        }

        // ---- 제보 만들기와 읽기 ----

        /// <summary>
        /// 제보 하나를 저장한다. 끝나면 done이 제보 ID와 함께 불린다. 화면을 찍으려고 프레임 끝까지 기다린다.
        /// title과 description은 비워도 된다.
        /// </summary>
        public static IEnumerator Create(string title, string description, Action<string> done)
        {
            DateTime now = DateTime.UtcNow;
            string id = now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 4);
            // 화면을 찍기 전에 모아 둔다. 찍는 사이에 들어온 조작이 섞이지 않게
            List<string> steps = UguiRecorder.History();
            string log;
            lock (s_Log) log = string.Join("\n", s_Log);

            // 배치 모드에서는 프레임 끝을 기다리는 코루틴이 돌아오지 않는다(화면을 그리지 않는다). 그때는 화면 없이 제보를 남긴다
            byte[] png = null;
            if (Application.isBatchMode) yield return null;
            else
            {
                yield return new WaitForEndOfFrame();
                try
                {
                    Texture2D shot = ScreenCapture.CaptureScreenshotAsTexture();
                    if (shot != null)
                    {
                        png = shot.EncodeToPNG();
                        Destroy(shot);
                    }
                }
                catch (Exception e)
                {
                    // 화면을 찍지 못해도 제보는 남긴다
                    Debug.LogWarning("[qaplay] report " + id + " has no screenshot: " + e.Message);
                }
            }

            var report = new Dictionary<string, object>
            {
                ["id"] = id,
                ["title"] = string.IsNullOrEmpty(title) ? "Report " + now.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : title,
                ["description"] = description ?? "",
                ["createdAt"] = now.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                ["app"] = new Dictionary<string, object> { ["id"] = Application.identifier, ["version"] = Application.version },
                ["unity"] = Application.unityVersion,
                ["platform"] = Application.platform.ToString(),
                ["device"] = new Dictionary<string, object> { ["model"] = SystemInfo.deviceModel, ["os"] = SystemInfo.operatingSystem },
                ["screen"] = new Dictionary<string, object> { ["width"] = Screen.width, ["height"] = Screen.height },
            };

            string folder = Path.Combine(Folder, id);
            Directory.CreateDirectory(folder);
            var utf8 = new UTF8Encoding(false);
            var yaml = new StringBuilder("# qaplay report " + id + ": the taps before the report, oldest first\n");
            foreach (string step in steps) yaml.Append(step).Append('\n');
            File.WriteAllText(Path.Combine(folder, "steps.yaml"), yaml.ToString(), utf8);
            File.WriteAllText(Path.Combine(folder, "log.txt"), log, utf8);
            if (png != null) File.WriteAllBytes(Path.Combine(folder, "screenshot.png"), png);
            // report.json을 마지막에 쓴다. 이 파일이 있어야 완성된 제보로 본다
            File.WriteAllText(Path.Combine(folder, "report.json"), Json.Write(report), utf8);
            done(id);
        }

        /// <summary>기기에 쌓인 제보의 report.json 내용을 오래된 것부터 돌려준다.</summary>
        public static List<Dictionary<string, object>> List()
        {
            var reports = new List<Dictionary<string, object>>();
            if (!Directory.Exists(Folder)) return reports;
            string[] folders = Directory.GetDirectories(Folder);
            Array.Sort(folders, StringComparer.Ordinal);
            foreach (string folder in folders)
            {
                string file = Path.Combine(folder, "report.json");
                if (File.Exists(file)) reports.Add((Dictionary<string, object>)Json.Parse(File.ReadAllText(file)));
            }
            return reports;
        }

        /// <summary>제보 하나의 내용 전체. 글자 파일은 글자로, 화면은 base64로 싣는다. 없는 ID면 unknown_report.</summary>
        public static Dictionary<string, object> Get(string id)
        {
            string folder = FolderOf(id);
            var result = new Dictionary<string, object>
            {
                ["report"] = Json.Parse(File.ReadAllText(Path.Combine(folder, "report.json"))),
                ["steps"] = File.ReadAllText(Path.Combine(folder, "steps.yaml")),
                ["log"] = File.ReadAllText(Path.Combine(folder, "log.txt")),
            };
            string shot = Path.Combine(folder, "screenshot.png");
            if (File.Exists(shot)) result["screenshot"] = Convert.ToBase64String(File.ReadAllBytes(shot));
            return result;
        }

        public static void Delete(string id) => Directory.Delete(FolderOf(id), true);

        static string FolderOf(string id)
        {
            // ID가 경로로 쓰이므로 글자를 제한한다(폴더 밖을 가리키지 못하게)
            bool valid = !string.IsNullOrEmpty(id);
            foreach (char c in id ?? "") valid &= (c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '-';
            if (!valid) throw new QaplayException("bad_request", "id must be a report id");
            string folder = Path.Combine(Folder, id);
            if (!File.Exists(Path.Combine(folder, "report.json")))
                throw new QaplayException("unknown_report", "no report with id " + id);
            return folder;
        }
    }
}
