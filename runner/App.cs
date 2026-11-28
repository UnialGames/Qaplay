using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Threading;

namespace Qaplay.Runner
{
    /// <summary>
    /// 러너가 직접 띄우는 게임. 죽거나 멈추면 로그를 챙기고 다시 띄워, 남은 테스트를 이어 갈 수 있게 한다.
    /// PC 빌드는 --app 실행 파일, Android는 --device 기기의 --package 앱이다.
    /// 둘 다 주지 않으면(이미 떠 있는 게임이나 에디터에 붙는 경우) 러너는 게임을 다시 띄울 수 없다.
    /// </summary>
    sealed class App
    {

        readonly string m_Path, m_ExtraArgs;         // PC 빌드
        readonly string m_Adb, m_Device, m_Package;  // Android
        readonly int m_Port;
        readonly string m_Artifacts;
        readonly int m_LaunchSeconds;
        readonly Func<AgentClient> m_Connect;
        Process m_Process;

        /// <summary>마지막으로 끝난 게임이 어떻게 끝났는지(종료 코드). 알 수 없으면 null.</summary>
        public string ExitNote { get; private set; }

        public bool HasExited => m_Process != null && m_Process.HasExited;

        /// <summary>러너가 띄운 PC 빌드가 지금까지 쓴 CPU 시간. 띄운 프로세스가 없거나 끝났으면 null.</summary>
        public TimeSpan? CpuTime
        {
            get
            {
                if (m_Process == null || m_Process.HasExited) return null;
                m_Process.Refresh();
                return m_Process.TotalProcessorTime;
            }
        }

        App(Args args, int port, Func<AgentClient> connect)
        {
            m_Path = args.Get("app", null);
            m_ExtraArgs = args.Get("app-args", "");
            m_Package = args.Get("package", null);
            m_Device = args.Get("device", null);
            if (m_Package != null) m_Adb = Adb.Find(args.Get("adb", null));
            m_Port = port;
            m_Artifacts = args.Get("artifacts", "qaplay-artifacts");
            // 게임이 뜨고 에이전트가 답하기까지 기다리는 시간. 큰 게임은 처음 뜨는 데 오래 걸린다
            m_LaunchSeconds = args.Int("launch-seconds", 60);
            m_Connect = connect;
        }

        /// <summary>--app도 --package도 없으면 null.</summary>
        public static App From(Args args, int port, Func<AgentClient> connect)
        {
            string path = args.Get("app", null), package = args.Get("package", null);
            if (path == null && package == null) return null;
            if (path != null && package != null) throw new UsageException("use either --app or --package, not both");
            if (path != null && !File.Exists(path)) throw new UsageException("no such file: " + path);
            if (package != null && args.Get("device", null) == null) throw new UsageException("--package needs --device");
            return new App(args, port, connect);
        }

        string LogPath => Path.GetFullPath(Path.Combine(m_Artifacts, "player.log"));

        /// <summary>게임을 띄우고 에이전트가 응답할 때까지 기다린다. 60초 안에 응답하지 않으면 IOException.</summary>
        public void Launch()
        {
            Directory.CreateDirectory(m_Artifacts);
            ExitNote = null;
            if (m_Path != null)
            {
                // 게임이 콘솔에 찍는 말은 러너의 출력에 섞이지 않게 받아서 버린다. 게임의 로그는 -logFile로 따로 남는다
                var start = new ProcessStartInfo(m_Path)
                {
                    UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                };
                // -silent-crashes: 죽었을 때 사람의 확인을 기다리는 창을 띄우지 않는다
                foreach (string argument in new[] { "-qaplay-port", m_Port.ToString(), "-silent-crashes", "-logFile", LogPath })
                    start.ArgumentList.Add(argument);
                foreach (string argument in m_ExtraArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    start.ArgumentList.Add(argument);
                m_Process = Process.Start(start);
                m_Process.BeginOutputReadLine();
                m_Process.BeginErrorReadLine();
            }
            else
            {
                Adb.Run(m_Adb, "-s", m_Device, "logcat", "-c"); // 이번 실행의 로그만 남도록 비운다
                // 이미 떠 있는 앱은 monkey가 앞으로 가져오기만 한다. 처음 상태에서 시작하도록 먼저 끝낸다
                Adb.Run(m_Adb, "-s", m_Device, "shell", "am", "force-stop", m_Package);
                PreventDialogs();
                Adb.Run(m_Adb, "-s", m_Device, "shell", "monkey", "-p", m_Package, "-c", "android.intent.category.LAUNCHER", "1");
            }

            long deadline = Environment.TickCount64 + m_LaunchSeconds * 1000L;
            while (true)
            {
                if (HasExited) throw new IOException("the game exited while starting (exit code " + m_Process.ExitCode + ")");
                try
                {
                    // 포트가 열렸는지가 아니라 에이전트가 hello에 답하는지를 본다(adb forward는 앱이 없어도 접속을 받는다)
                    m_Connect().Dispose();
                    return;
                }
                catch (Exception e) when (e is IOException || e is SocketException)
                {
                    if (Environment.TickCount64 > deadline)
                        throw new IOException("the game did not answer on port " + m_Port + " within " + m_LaunchSeconds + " s (raise run.launchSeconds in the settings if the game needs longer): " + e.Message);
                    Thread.Sleep(250);
                }
            }
        }

        // OS가 띄우는 창은 게임 밖이라 에이전트가 누를 수 없고, 떠 있는 동안 게임이 멈춰 러너가 붙지 못한다.
        // 뜬 것을 찾아 누르지 않고, 뜨지 않게 미리 해 둔다: 전체 화면 앱의 첫 실행 안내를 본 것으로 해 두고, 앱이 요청할 권한을 미리 준다.
        // 기기의 설정과 앱의 권한이 바뀐 채로 남는다(QA 기기에서 쓰는 것을 전제로 한다)
        void PreventDialogs()
        {
            Adb.Run(m_Adb, "-s", m_Device, "shell", "settings", "put", "secure", "immersive_mode_confirmations", "confirmed");
            List<string> permissions = Adb.RequestedPermissions(Adb.Run(m_Adb, "-s", m_Device, "shell", "dumpsys", "package", m_Package));
            if (permissions.Count == 0) return;
            // 설치할 때 정해지는 권한(INTERNET 등)은 줄 수 없다고 나온다. 그런 실패는 넘어간다: 줄 수 있는 것만 주면 된다
            var script = new System.Text.StringBuilder();
            foreach (string permission in permissions)
                script.Append("pm grant ").Append(m_Package).Append(' ').Append(permission).Append(" 2>/dev/null; ");
            Adb.Run(m_Adb, "-s", m_Device, "shell", script.Append("true").ToString());
        }

        /// <summary>
        /// 게임이 죽었거나 죽는 중인지. 죽는 중인 게임은 크래시 기록을 쓰는 동안 연결이 끊기지 않아
        /// 러너에는 프리징처럼 보인다. 그 둘을 게임의 로그에 남은 크래시 표시로 구분한다.
        /// </summary>
        public bool LooksCrashed()
        {
            try
            {
                if (HasExited) return true;
                string log;
                if (m_Path == null) log = Adb.Run(m_Adb, "-s", m_Device, "logcat", "-d");
                else
                {
                    // 게임이 쓰고 있는 파일이므로 함께 여는 방식으로 읽는다
                    using var stream = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream);
                    log = reader.ReadToEnd();
                }
                // Unity의 PC 빌드는 "Crash!!!", Android는 "Fatal signal"(네이티브)이나 "FATAL EXCEPTION"(자바)을 남긴다
                return log.Contains("Crash!!!") || log.Contains("Fatal signal") || log.Contains("FATAL EXCEPTION");
            }
            catch (Exception e) when (e is IOException || e is UsageException)
            {
                return false;
            }
        }

        /// <summary>
        /// 게임을 끝낸다. saveLogAs가 있으면 그 이름으로 게임의 로그를 남기고 저장한 경로를 돌려준다.
        /// crashed이면 게임이 크래시 기록을 다 쓸 때까지 잠깐 기다린 뒤에 끝낸다.
        /// </summary>
        public string Stop(string saveLogAs, bool crashed)
        {
            string saved = saveLogAs == null ? null : Path.GetFullPath(Path.Combine(m_Artifacts, saveLogAs));
            if (m_Path != null)
            {
                if (m_Process != null)
                {
                    try
                    {
                        if (crashed) m_Process.WaitForExit(10000);
                        if (m_Process.HasExited) ExitNote = "the game exited with code " + m_Process.ExitCode;
                        else
                        {
                            m_Process.Kill(true);
                            m_Process.WaitForExit(10000);
                        }
                    }
                    catch (Exception e) when (e is InvalidOperationException || e is System.ComponentModel.Win32Exception)
                    {
                        // 이미 끝난 프로세스
                    }
                    m_Process.Dispose();
                    m_Process = null;
                }
                if (saved == null) return null;
                if (!File.Exists(LogPath)) return null;
                File.Copy(LogPath, saved, true);
            }
            else
            {
                if (saved != null) File.WriteAllText(saved, Adb.Run(m_Adb, "-s", m_Device, "logcat", "-d"));
                Adb.Run(m_Adb, "-s", m_Device, "shell", "am", "force-stop", m_Package);
            }
            return saved;
        }
    }
}
