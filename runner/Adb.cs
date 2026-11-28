using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace Qaplay.Runner
{
    /// <summary>Android 기기를 다루는 adb 호출: 기기 목록과 포트 연결(adb forward).</summary>
    static class Adb
    {
        public struct Device
        {
            public string serial, state, model;
        }

        /// <summary>adb를 찾는다. 순서: --adb로 준 경로, PATH의 adb, Android SDK 폴더(환경 변수, 그다음 기본 설치 위치).</summary>
        public static string Find(string given)
        {
            if (given != null) return given;
            string exe = OperatingSystem.IsWindows() ? "adb.exe" : "adb";
            var folders = new List<string>((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator));
            foreach (string sdk in new[]
                     {
                         Environment.GetEnvironmentVariable("ANDROID_HOME"),
                         Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT"),
                         Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk"),
                         Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Android", "sdk"),
                         Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Android", "Sdk"),
                     })
                if (!string.IsNullOrEmpty(sdk)) folders.Add(Path.Combine(sdk, "platform-tools"));

            foreach (string folder in folders)
                if (folder.Length > 0 && File.Exists(Path.Combine(folder, exe)))
                    return Path.Combine(folder, exe);
            throw new UsageException("adb was not found; install Android platform-tools or pass --adb PATH");
        }

        static readonly HashSet<string> s_ServerStarted = new HashSet<string>();

        // adb의 서버(데몬)는 처음 쓸 때 저절로 뜬다. Windows에서는 그때 서버가 이 러너가 물려받은 출력 통로(러너를 부른 프로그램의 것)까지
        // 물려받아 쥐고 있는다. 그러면 러너를 부른 쪽(Unity의 설정 화면, 테스트, CI)이 러너의 출력이 끝나기를 영영 기다린다
        // (기기 목록을 처음 볼 때 Unity의 테스트가 한 시간 넘게 멈췄다). 그래서 서버는 통로를 물려주지 않는 셸 실행으로 먼저 띄운다
        static void StartServer(string adb)
        {
            if (!OperatingSystem.IsWindows() || !s_ServerStarted.Add(adb)) return;
            try
            {
                using Process process = Process.Start(new ProcessStartInfo(adb, "start-server") { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden });
                process?.WaitForExit(30000);
            }
            catch (Win32Exception)
            {
                // adb를 실행할 수 없으면 아래에서 알린다
            }
        }

        /// <summary>adb를 실행하고 출력을 돌려준다. 실패하면 adb가 한 말을 그대로 실어 UsageException.</summary>
        public static string Run(string adb, params string[] arguments)
        {
            StartServer(adb);
            var start = new ProcessStartInfo(adb)
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
            };
            foreach (string argument in arguments) start.ArgumentList.Add(argument);
            try
            {
                using Process process = Process.Start(start);
                string output = process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode != 0)
                    throw new UsageException("adb " + string.Join(" ", arguments) + " failed: " + (error + output).Trim());
                return output;
            }
            catch (Win32Exception e)
            {
                throw new UsageException("cannot run " + adb + ": " + e.Message);
            }
        }

        /// <summary>
        /// "dumpsys package 패키지"의 출력에서 앱이 매니페스트에 적은 권한을 읽는다("requested permissions:" 아래의 줄들).
        /// 다음 구역("install permissions:" 등)이 시작되면 끝난다.
        /// </summary>
        public static List<string> RequestedPermissions(string output)
        {
            var permissions = new List<string>();
            bool inside = false;
            foreach (string raw in output.Split('\n'))
            {
                string line = raw.Trim();
                if (line == "requested permissions:") inside = true;
                else if (inside && line.EndsWith(":", StringComparison.Ordinal)) break;
                // 권한 이름 뒤에 ": restricted=true" 같은 표시가 붙기도 한다
                else if (inside && line.Length > 0) permissions.Add(line.Split(':')[0]);
            }
            return permissions;
        }

        /// <summary>"adb devices -l"의 출력을 읽는다. 첫 줄(제목)과 데몬 안내 줄(*로 시작)은 건너뛴다.</summary>
        public static List<Device> Parse(string output)
        {
            var devices = new List<Device>();
            foreach (string line in output.Split('\n'))
            {
                string[] parts = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2 || parts[0].StartsWith("*", StringComparison.Ordinal) || parts[0] == "List") continue;
                var device = new Device { serial = parts[0], state = parts[1], model = "" };
                foreach (string part in parts)
                    if (part.StartsWith("model:", StringComparison.Ordinal))
                        device.model = part.Substring("model:".Length);
                devices.Add(device);
            }
            return devices;
        }

        /// <summary>
        /// 쓸 수 있는(state가 device인) 기기가 하나뿐이면 그 시리얼. 없거나 여럿이면 무엇을 하면 되는지와 함께 알린다.
        /// 설정에 기기를 적지 않았을 때 쓴다.
        /// </summary>
        public static string Only(List<Device> devices)
        {
            List<Device> ready = devices.FindAll(d => d.state == "device");
            if (ready.Count == 1) return ready[0].serial;
            if (ready.Count == 0)
                throw new UsageException("no Android device is connected" + (devices.Count > 0 ? " (" + devices[0].serial + " is " + devices[0].state + ")" : "")
                                         + ": connect one with USB debugging on, or start the emulator");
            throw new UsageException(ready.Count + " Android devices are connected (" + string.Join(", ", ready.ConvertAll(d => d.serial))
                                     + "): pick one in the settings (Unity: Project Settings > qaplay) or give --device SERIAL");
        }
    }
}
