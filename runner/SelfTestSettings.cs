using System;
using System.Collections.Generic;
using System.IO;

namespace Qaplay.Runner
{
    /// <summary>프로젝트 설정(qaplay.json)의 검사: 값을 정하는 순서, 실행 대상, 경로, 오타, 기기 고르기.</summary>
    static class SettingsSelfTest
    {
        static void Check(bool condition, string what)
        {
            if (!condition) throw new InvalidOperationException("selftest failed: " + what);
        }

        static Args With(string file, params string[] argv)
        {
            var list = new List<string>(argv) { "--settings", file };
            var args = new Args(list.ToArray());
            ProjectSettings.Load(args).ApplyTo(args);
            return args;
        }

        public static void Run()
        {
            string folder = Path.Combine(Path.GetTempPath(), "qaplay-selftest-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(folder, "Assets", "Rules"));
                string file = Path.Combine(folder, "qaplay.json");

                // 설정이 없으면 모두 기본값이다
                Args none = With(file, "run");
                Check(none.Get("app", null) == null && none.Get("device", null) == null && none.Int("retries", 1) == 1 && none.Get("reports", "qaplay-reports") == "qaplay-reports",
                    "without a settings file the defaults are used");

                // PC 빌드: 경로는 설정 파일의 폴더 기준, 명령줄이 이긴다
                File.WriteAllText(file, "{\"target\": {\"kind\": \"pc\", \"app\": \"Builds/Game.exe\", \"appArgs\": \"-screen-width 640\", \"package\": \"com.unused\"},"
                                        + " \"rules\": [\"Assets/Rules\"], \"run\": {\"retries\": 3, \"launchSeconds\": 120}, \"reports\": \"../reports\"}");
                Args pc = With(file, "run", "--retries", "0");
                Check(pc.Get("app", null) == Path.Combine(folder, "Builds", "Game.exe") && pc.Get("app-args", null) == "-screen-width 640",
                    "a pc target gives the build to start, relative to the settings file");
                Check(pc.Get("package", null) == null, "a pc target gives no android values even when they are written");
                Check(pc.Int("retries", 1) == 0 && pc.Int("launch-seconds", 60) == 120, "the command line wins over the settings, and the settings over the defaults");
                Check(pc.Get("reports", null) == Path.GetFullPath(Path.Combine(folder, "..", "reports")), "folders are relative to the settings file");
                Check(ProjectSettings.Load(file).Rules[0] == Path.Combine(folder, "Assets", "Rules"), "rules folders are relative to the settings file");
                // 명령줄에 Android를 주면 설정의 PC 빌드는 쓰지 않는다
                Args overridden = With(file, "run", "--package", "com.game", "--device", "emulator-5554");
                Check(overridden.Get("app", null) == null && overridden.Get("package", null) == "com.game", "a target on the command line replaces the one in the settings");

                // Android: 기기를 적지 않았으면 처음 쓸 때 연결된 기기 하나를 찾는다. 기기를 쓰지 않는 명령은 찾지 않는다
                File.WriteAllText(file, "{\"target\": {\"kind\": \"android\", \"package\": \"com.game\", \"device\": \"\"}}");
                Args android = With(file, "run", "--adb", Path.Combine(folder, "no-adb.exe"));
                Check(android.Get("package", null) == "com.game", "an android target gives the package");
                try { android.Get("device", null); Check(false, "a device is looked up only when it is used"); }
                catch (Exception e) when (e is UsageException || e is IOException) { }
                string pinnedFile = Path.Combine(folder, "pinned.json");
                File.WriteAllText(pinnedFile, "{\"target\": {\"kind\": \"android\", \"package\": \"com.game\", \"device\": \"R5CW123\"}}");
                Args pinned = With(pinnedFile, "run");
                Check(pinned.Get("device", null) == "R5CW123", "a device written in the settings is used as it is");
                Check(With(file, "run", "--app", "Game.exe").Get("package", null) == null, "--app on the command line drops the android target");

                List<Adb.Device> Devices(string output) => Adb.Parse("List of devices attached\n" + output);
                Check(Adb.Only(Devices("emulator-5554\tdevice product:x model:Pixel\n")) == "emulator-5554", "the one device connected is picked");
                Check(Adb.Only(Devices("R5CW123\tunauthorized\nemulator-5554\tdevice\n")) == "emulator-5554", "a device that is not ready is not counted");
                try { Adb.Only(Devices("")); Check(false, "no device is reported"); }
                catch (UsageException e) { Check(e.Message.StartsWith("no Android device is connected"), "with what to do"); }
                try { Adb.Only(Devices("a\tdevice\nb\tdevice\n")); Check(false, "two devices are not guessed between"); }
                catch (UsageException e) { Check(e.Message.Contains("(a, b)") && e.Message.Contains("--device"), "the message lists them and says how to pick"); }

                // 오타와 틀린 값은 읽을 때 알린다
                foreach (string bad in new[]
                         {
                             "{\"rule\": [\"Assets\"]}", "{\"target\": {\"kind\": \"ios\"}}", "{\"target\": {\"packge\": \"x\"}}", "{\"run\": {\"retries\": -1}}",
                             "{\"run\": {\"retries\": 1.5}}", "{\"run\": {\"retry\": 1}}", "{\"rules\": \"Assets\"}", "{\"reports\": 3}", "[1, 2]",
                         })
                {
                    File.WriteAllText(file, bad);
                    try { ProjectSettings.Load(file); Check(false, "a wrong setting is refused: " + bad); }
                    catch (UsageException e) { Check(e.Message.StartsWith("cannot read " + file), "the message names the file: " + bad); }
                }

                // 저장: 한 부분만 바꾸고 나머지는 그대로, 사람이 읽기 좋게, 한글은 그대로
                File.WriteAllText(file, "{\"target\": {\"kind\": \"editor\"}, \"rules\": [\"Assets/Rules\"]}");
                ProjectSettings settings = ProjectSettings.Load(file);
                settings.Set("sheet", new Dictionary<string, object> { ["type"] = "csv", ["tc"] = "tc.csv", ["columns"] = new Dictionary<string, object> { ["tc"] = "TC 번호" } });
                settings.Save();
                string written = File.ReadAllText(file);
                Check(written.Contains("\n  \"rules\": [\"Assets/Rules\"],\n") && written.Contains("\"tc\": \"TC 번호\"") && written.EndsWith("}\n"), "the file stays readable");
                ProjectSettings again = ProjectSettings.Load(file);
                Check(again.Kind == "editor" && again.Sheet["tc"].Equals("tc.csv") && again.RulesAsWritten[0] == "Assets/Rules", "what was there before is kept");
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }
    }
}
