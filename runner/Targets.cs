using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Qaplay.Runner
{
    /// <summary>
    /// 게임이 어디서 도는지(전략): 에디터의 Play나 이미 떠 있는 게임(붙기만 한다), 러너가 띄우는 PC 빌드, Android 기기.
    /// 러너는 이것을 통해 게임에 닿고(연결 준비), 띄우고, 죽거나 멈춘 게임의 로그를 챙겨 다시 띄우고, OS에서만 알 수 있는 지표를 읽는다.
    /// 쓰는 쪽(실행, 기기 지표, Python 중계)은 어느 것인지 모른다. 새 대상(iOS 등)은 이것을 하나 더 만들어 From에 넣는다.
    /// </summary>
    abstract class GameTarget
    {
        /// <summary>사람에게 보여 줄 대상의 종류: editor, pc, android.</summary>
        public abstract string Kind { get; }

        /// <summary>러너가 게임을 띄우고, 죽거나 멈추면 다시 띄울 수 있는지. 붙기만 하는 대상은 아니다.</summary>
        public virtual bool CanLaunch => false;

        /// <summary>마지막으로 끝난 게임이 어떻게 끝났는지(종료 코드). 알 수 없으면 null.</summary>
        public string ExitNote { get; protected set; }

        /// <summary>러너가 띄운 게임이 스스로 끝났는지.</summary>
        public virtual bool HasExited => false;

        /// <summary>러너가 그 포트로 게임에 닿을 수 있게 해 둔다(Android: 이 PC의 포트를 기기의 에이전트로 잇는다).</summary>
        public virtual void Prepare(int port) { }

        /// <summary>게임을 띄우고 에이전트가 답할 때까지 기다린다. 띄울 수 없는 대상이면 아무것도 하지 않는다.</summary>
        public virtual void Launch() { }

        /// <summary>게임이 죽었거나 죽는 중인지. 죽는 중인 게임은 크래시 기록을 쓰는 동안 연결이 끊기지 않아 프리징처럼 보인다.</summary>
        public virtual bool LooksCrashed() => false;

        /// <summary>
        /// 게임을 끝낸다. saveLogAs가 있으면 그 이름으로 게임의 로그를 남기고 저장한 경로를 돌려준다.
        /// crashed이면 게임이 크래시 기록을 다 쓸 때까지 잠깐 기다린 뒤에 끝낸다.
        /// </summary>
        public virtual string Stop(string saveLogAs, bool crashed) => null;

        /// <summary>OS에서 읽는 지표. 읽지 못한 것은 null로 둔다. package는 게임이 알려 준 앱의 이름이다.</summary>
        public virtual DeviceMeter.Sample ReadOs(bool startOnly, string package, double clock) => default;

        /// <summary>
        /// 명령의 값(명령줄과 프로젝트 설정)에서 대상을 고른다. 대상을 가르는 조건은 여기에만 있다.
        /// connect는 띄운 게임의 에이전트가 답하는지 볼 때 쓴다.
        /// </summary>
        public static GameTarget From(Args args, int port, Func<AgentClient> connect)
        {
            string path = args.Get("app", null), package = args.Get("package", null);
            if (path != null && package != null) throw new UsageException("use either --app or --package, not both");
            var launch = new Launching(args, port, connect);
            // 빌드 파일이 있는지는 띄울 때 본다: 이미 떠 있는 빌드에 붙기만 하는 명령(dump 등)은 그 파일이 없어도 된다
            if (path != null) return new PcBuildTarget(path, args.Get("app-args", ""), launch);
            string device = args.Get("device", null);
            if (package != null && device == null) throw new UsageException("--package needs --device");
            if (device != null) return new AndroidTarget(Adb.Find(args.Get("adb", null)), device, package, launch);
            return new AttachedTarget();
        }

        /// <summary>띄우는 대상들이 함께 쓰는 값: 포트, 로그를 받을 폴더, 기다리는 시간, 에이전트에 닿아 보는 방법.</summary>
        internal sealed class Launching
        {
            public readonly int port, seconds;
            public readonly string artifacts;
            public readonly Func<AgentClient> connect;

            public Launching(Args args, int port, Func<AgentClient> connect)
            {
                this.port = port;
                this.connect = connect;
                artifacts = args.Get("artifacts", "qaplay-artifacts");
                // 게임이 뜨고 에이전트가 답하기까지 기다리는 시간. 큰 게임은 처음 뜨는 데 오래 걸린다
                seconds = args.Int("launch-seconds", 60);
            }

            public string Saved(string name) => name == null ? null : Path.GetFullPath(Path.Combine(artifacts, name));

            /// <summary>에이전트가 hello에 답할 때까지 기다린다. 게임이 그 사이에 끝나면(exited가 종료 코드를 주면) 바로 알린다.</summary>
            public void WaitForAgent(Func<int?> exited)
            {
                long deadline = Environment.TickCount64 + seconds * 1000L;
                while (true)
                {
                    int? code = exited();
                    if (code != null) throw new IOException("the game exited while starting (exit code " + code + ")");
                    try
                    {
                        // 포트가 열렸는지가 아니라 에이전트가 hello에 답하는지를 본다(adb forward는 앱이 없어도 접속을 받는다)
                        connect().Dispose();
                        return;
                    }
                    catch (Exception e) when (e is IOException || e is SocketException)
                    {
                        if (Environment.TickCount64 > deadline)
                            throw new IOException("the game did not answer on port " + port + " within " + seconds
                                                  + " s (raise run.launchSeconds in the settings if the game needs longer): " + e.Message);
                        Thread.Sleep(250);
                    }
                }
            }
        }
    }

    /// <summary>에디터의 Play나 이미 떠 있는 게임: 러너는 붙기만 한다. 띄우거나 다시 띄울 수 없다.</summary>
    sealed class AttachedTarget : GameTarget
    {
        public override string Kind => "editor";
    }

    /// <summary>러너가 띄우는 PC 빌드(-qaplay-port로 포트를 알려 주고, 로그는 -logFile로 따로 받는다).</summary>
    sealed class PcBuildTarget : GameTarget
    {
        readonly string m_Path, m_ExtraArgs;
        readonly Launching m_Launch;
        Process m_Process;

        public PcBuildTarget(string path, string extraArgs, Launching launch)
        {
            m_Path = path;
            m_ExtraArgs = extraArgs;
            m_Launch = launch;
        }

        public override string Kind => "pc";
        public override bool CanLaunch => true;
        public override bool HasExited => m_Process != null && m_Process.HasExited;

        string LogPath => m_Launch.Saved("player.log");

        public override void Launch()
        {
            if (!File.Exists(m_Path)) throw new UsageException("no such file: " + m_Path);
            Directory.CreateDirectory(m_Launch.artifacts);
            ExitNote = null;
            // 게임이 콘솔에 찍는 말은 러너의 출력에 섞이지 않게 받아서 버린다. 게임의 로그는 -logFile로 따로 남는다
            var start = new ProcessStartInfo(m_Path) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            // -silent-crashes: 죽었을 때 사람의 확인을 기다리는 창을 띄우지 않는다
            foreach (string argument in new[] { "-qaplay-port", m_Launch.port.ToString(), "-silent-crashes", "-logFile", LogPath })
                start.ArgumentList.Add(argument);
            foreach (string argument in m_ExtraArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                start.ArgumentList.Add(argument);
            m_Process = Process.Start(start);
            m_Process.BeginOutputReadLine();
            m_Process.BeginErrorReadLine();
            m_Launch.WaitForAgent(() => HasExited ? m_Process.ExitCode : (int?)null);
        }

        public override bool LooksCrashed()
        {
            try
            {
                if (HasExited) return true;
                // 게임이 쓰고 있는 파일이므로 함께 여는 방식으로 읽는다
                using var stream = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                // Unity의 PC 빌드는 죽을 때 "Crash!!!"를 남긴다
                return reader.ReadToEnd().Contains("Crash!!!");
            }
            catch (IOException)
            {
                return false;
            }
        }

        public override string Stop(string saveLogAs, bool crashed)
        {
            string saved = m_Launch.Saved(saveLogAs);
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
            if (saved == null || !File.Exists(LogPath)) return null;
            File.Copy(LogPath, saved, true);
            return saved;
        }

        // 러너가 띄운 프로세스가 지금까지 쓴 CPU 시간. 이 PC의 다른 값(발열, 배터리)은 읽지 않는다
        public override DeviceMeter.Sample ReadOs(bool startOnly, string package, double clock)
        {
            if (m_Process == null || m_Process.HasExited) return default;
            m_Process.Refresh();
            return new DeviceMeter.Sample { cpuSeconds = m_Process.TotalProcessorTime.TotalSeconds, clock = clock };
        }
    }

    /// <summary>
    /// Android 기기. 패키지를 알면 러너가 앱을 띄우고 다시 띄운다. 모르면 기기에서 떠 있는 앱에 붙기만 한다.
    /// 이 PC의 포트를 기기의 에이전트 포트로 이어(adb forward) 그리로 이야기한다.
    /// </summary>
    sealed class AndroidTarget : GameTarget
    {
        readonly string m_Adb, m_Device, m_Package;
        readonly Launching m_Launch;

        public AndroidTarget(string adb, string device, string package, Launching launch)
        {
            m_Adb = adb;
            m_Device = device;
            m_Package = package;
            m_Launch = launch;
        }

        public override string Kind => "android";
        public override bool CanLaunch => m_Package != null;

        string Run(params string[] arguments)
        {
            var all = new List<string> { "-s", m_Device };
            all.AddRange(arguments);
            return Adb.Run(m_Adb, all.ToArray());
        }

        public override void Prepare(int port) => Run("forward", "tcp:" + port, "tcp:" + Program.AgentPort);

        public override void Launch()
        {
            if (m_Package == null) return;
            Directory.CreateDirectory(m_Launch.artifacts);
            ExitNote = null;
            Run("logcat", "-c"); // 이번 실행의 로그만 남도록 비운다
            // 이미 떠 있는 앱은 monkey가 앞으로 가져오기만 한다. 처음 상태에서 시작하도록 먼저 끝낸다
            Run("shell", "am", "force-stop", m_Package);
            PreventDialogs();
            Run("shell", "monkey", "-p", m_Package, "-c", "android.intent.category.LAUNCHER", "1");
            m_Launch.WaitForAgent(() => null);
        }

        // OS가 띄우는 창은 게임 밖이라 에이전트가 누를 수 없고, 떠 있는 동안 게임이 멈춰 러너가 붙지 못한다.
        // 뜬 것을 찾아 누르지 않고, 뜨지 않게 미리 해 둔다: 전체 화면 앱의 첫 실행 안내를 본 것으로 해 두고, 앱이 요청할 권한을 미리 준다.
        // 기기의 설정과 앱의 권한이 바뀐 채로 남는다(QA 기기에서 쓰는 것을 전제로 한다)
        void PreventDialogs()
        {
            Run("shell", "settings", "put", "secure", "immersive_mode_confirmations", "confirmed");
            List<string> permissions = Adb.RequestedPermissions(Run("shell", "dumpsys", "package", m_Package));
            if (permissions.Count == 0) return;
            // 설치할 때 정해지는 권한(INTERNET 등)은 줄 수 없다고 나온다. 그런 실패는 넘어간다: 줄 수 있는 것만 주면 된다
            var script = new StringBuilder();
            foreach (string permission in permissions)
                script.Append("pm grant ").Append(m_Package).Append(' ').Append(permission).Append(" 2>/dev/null; ");
            Run("shell", script.Append("true").ToString());
        }

        public override bool LooksCrashed()
        {
            try
            {
                // 네이티브 크래시는 "Fatal signal", 자바 쪽 크래시는 "FATAL EXCEPTION"을 남긴다
                string log = Run("logcat", "-d");
                return log.Contains("Fatal signal") || log.Contains("FATAL EXCEPTION");
            }
            catch (Exception e) when (e is IOException || e is UsageException)
            {
                return false;
            }
        }

        public override string Stop(string saveLogAs, bool crashed)
        {
            if (m_Package == null) return null;
            string saved = m_Launch.Saved(saveLogAs);
            if (saved != null) File.WriteAllText(saved, Run("logcat", "-d"));
            Run("shell", "am", "force-stop", m_Package);
            return saved;
        }

        public override DeviceMeter.Sample ReadOs(bool startOnly, string package, double clock)
        {
            string app = m_Package ?? package;
            // 한 번의 호출로 모은다. 시각은 기기의 것을 쓴다: adb가 오가는 시간이 CPU 계산에 섞이지 않는다
            string script = "echo TCK; getconf CLK_TCK; echo UPTIME; cat /proc/uptime; echo STAT; cat /proc/$(pidof " + app + ")/stat";
            if (!startOnly)
                script += "; dumpsys thermalservice | grep 'Thermal Status'; dumpsys battery; dumpsys meminfo -s " + app;
            return DeviceMeter.Parse(Run("shell", script));
        }
    }
}
