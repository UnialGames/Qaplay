using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Qaplay.Editor
{
    /// <summary>
    /// 에디터에서 러너(qaplay 명령)를 찾아 부른다. 시트와 Google에 닿는 일은 러너가 하고, 에디터의 창은 그 결과를 보여 준다.
    /// 같은 일을 두 벌로 만들지 않으려는 것이다.
    /// </summary>
    public static class QaplayRunner
    {
        const string PrefKey = "qaplay.runner";

        public sealed class Result
        {
            public int code;
            /// <summary>표준 출력(--json이면 JSON 하나)과 표준 오류(사람이 읽는 줄).</summary>
            public string output = "", error = "";
            /// <summary>표준 출력이 JSON 객체였으면 그것. 아니면 null.</summary>
            public Dictionary<string, object> json;
        }

        static string FileName => Application.platform == RuntimePlatform.WindowsEditor ? "qaplay.exe" : "qaplay";

        /// <summary>사람이 직접 고른 러너의 경로. 비어 있으면 Find가 찾는다.</summary>
        public static string Chosen
        {
            get => EditorPrefs.GetString(PrefKey, "");
            set => EditorPrefs.SetString(PrefKey, value ?? "");
        }

        /// <summary>
        /// 러너를 찾는다. 찾는 순서: 직접 고른 경로, 환경 변수 QAPLAY_RUNNER, 이 프로젝트의 위쪽 폴더들에 있는 runner 빌드 결과
        /// (qaplay 저장소 안에서 쓰는 경우), PATH. 없으면 null.
        /// </summary>
        public static string Find()
        {
            foreach (string candidate in new[] { Chosen, Environment.GetEnvironmentVariable("QAPLAY_RUNNER") })
                if (!string.IsNullOrEmpty(candidate) && File.Exists(candidate)) return candidate;

            DirectoryInfo folder = Directory.GetParent(Application.dataPath);
            for (int up = 0; up < 4 && folder != null; up++, folder = folder.Parent)
                foreach (string build in new[] { "Release", "Debug" })
                {
                    string candidate = Path.Combine(folder.FullName, "runner", "bin", build, "net10.0", FileName);
                    if (File.Exists(candidate)) return candidate;
                }

            foreach (string place in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                if (place.Trim().Length == 0) continue;
                try
                {
                    string candidate = Path.Combine(place.Trim(), FileName);
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException)
                {
                    // PATH에 경로로 쓸 수 없는 글자가 든 항목
                }
            }
            return null;
        }

        // 명령줄의 인자 하나. 빈칸이나 따옴표가 있으면 따옴표로 싼다(Windows의 규칙: 따옴표 앞의 역슬래시는 두 배로)
        static string Quote(string argument)
        {
            if (argument.Length > 0 && argument.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return argument;
            var quoted = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in argument)
            {
                if (c == '\\') slashes++;
                else
                {
                    if (c == '"') quoted.Append('\\', slashes + 1);
                    slashes = 0;
                }
                quoted.Append(c);
            }
            return quoted.Append('\\', slashes).Append('"').ToString();
        }

        /// <summary>러너를 실행하고 끝날 때까지 기다린다(최대 60초). 러너가 없으면 code가 -1이다.</summary>
        public static Result Run(string workingFolder, params string[] arguments)
        {
            var result = new Result();
            string runner = Find();
            if (runner == null)
            {
                result.code = -1;
                result.error = "the qaplay runner was not found";
                return result;
            }
            var start = new ProcessStartInfo(runner)
            {
                Arguments = string.Join(" ", Array.ConvertAll(arguments, Quote)),
                WorkingDirectory = workingFolder,
                UseShellExecute = false, CreateNoWindow = true,
                // 입력을 닫아 둔다: 러너가 무엇을 물으려 해도 기다리지 않고 "답할 사람이 없다"로 끝난다
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            };
            try
            {
                using (Process process = Process.Start(start))
                {
                    var error = new StringBuilder();
                    process.ErrorDataReceived += (sender, line) => { if (line.Data != null) lock (error) error.AppendLine(line.Data); };
                    process.BeginErrorReadLine();
                    process.StandardInput.Close();
                    string output = process.StandardOutput.ReadToEnd();
                    if (!process.WaitForExit(60000))
                    {
                        process.Kill();
                        result.code = -1;
                        result.error = "the runner did not finish in 60 seconds";
                        return result;
                    }
                    process.WaitForExit(); // 표준 오류를 끝까지 받는다
                    result.code = process.ExitCode;
                    result.output = output.Trim();
                    lock (error) result.error = error.ToString().Trim();
                }
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception || e is IOException || e is InvalidOperationException)
            {
                result.code = -1;
                result.error = "could not start " + runner + ": " + e.Message;
                return result;
            }
            if (result.output.StartsWith("{"))
            {
                try
                {
                    result.json = Json.Parse(result.output) as Dictionary<string, object>;
                }
                catch (FormatException)
                {
                    // JSON이 아닌 출력
                }
            }
            return result;
        }
    }
}
