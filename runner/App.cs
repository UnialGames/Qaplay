using System;
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
        const int LaunchTimeoutMs = 60000;

        readonly string m_Path, m_ExtraArgs;         // PC 빌드
        readonly string m_Adb, m_Device, m_Package;  // Android
        readonly int m_Port;
        readonly string m_Artifacts;
        readonly Func<AgentClient> m_Connect;
        Process m_Process;

        /// <summary>마지막으로 끝난 게임이 어떻게 끝났는지(종료 코드). 알 수 없으면 null.</summary>
        public string ExitNote { get; private set; }

        public bool HasExited => m_Process != null && m_Process.HasExited;

        App(Args args, int port, Func<AgentClient> connect)
        {
            m_Path = args.Get("app", null);
            m_ExtraArgs = args.Get("app-args", "");
            m_Package = args.Get("package", null);
            m_Device = args.Get("device", null);
            if (m_Package != null) m_Adb = Adb.Find(args.Get("adb", null));
            m_Port = port;
            m_Artifacts = args.Get("artifacts", "qaplay-artifacts");
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
                var start = new ProcessStartInfo(m_Path) { UseShellExecute = false };
                // -silent-crashes: 죽었을 때 사람의 확인을 기다리는 창을 띄우지 않는다
                foreach (string argument in new[] { "-qaplay-port", m_Port.ToString(), "-silent-crashes", "-logFile", LogPath })
                    start.ArgumentList.Add(argument);
                foreach (string argument in m_ExtraArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    start.ArgumentList.Add(argument);
                m_Process = Process.Start(start);
            }
            else
            {
                Adb.Run(m_Adb, "-s", m_Device, "logcat", "-c"); // 이번 실행의 로그만 남도록 비운다
                Adb.Run(m_Adb, "-s", m_Device, "shell", "monkey", "-p", m_Package, "-c", "android.intent.category.LAUNCHER", "1");
            }

            long deadline = Environment.TickCount64 + LaunchTimeoutMs;
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
                        throw new IOException("the game did not answer on port " + m_Port + " within " + LaunchTimeoutMs / 1000 + " s: " + e.Message);
                    Thread.Sleep(250);
                }
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
