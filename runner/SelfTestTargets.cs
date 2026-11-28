using System;
using System.Collections.Generic;
using System.IO;

namespace Qaplay.Runner
{
    /// <summary>실행 대상과 결과 출력의 검사: 무엇을 고르는지, 순서, 하나가 실패해도 나머지가 남는지.</summary>
    static class TargetsSelfTest
    {
        static void Check(bool condition, string what)
        {
            if (!condition) throw new InvalidOperationException("selftest failed: " + what);
        }

        /// <summary>검사용 출력: 받은 순서를 적고, 시키면 실패한다.</summary>
        sealed class Recorder : IRunOutput
        {
            readonly List<string> m_Log;
            readonly bool m_Fail;

            public Recorder(string name, List<string> log, bool fail)
            {
                Name = name;
                m_Log = log;
                m_Fail = fail;
            }

            public string Name { get; }

            public void Write(RunRecord run)
            {
                m_Log.Add(Name + ":" + (run.reportId ?? "-"));
                if (m_Fail) throw new IOException("disk full");
                run.reportId ??= "r1";
            }
        }

        public static void Run()
        {
            string folder = Path.Combine(Path.GetTempPath(), "qaplay-selftest-" + Guid.NewGuid().ToString("N"));
            string oldSummary = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY"), oldOutput = Environment.GetEnvironmentVariable("GITHUB_OUTPUT");
            TextWriter error = Console.Error;
            try
            {
                Directory.CreateDirectory(folder);
                string adb = Path.Combine(folder, "adb.exe");
                File.WriteAllText(adb, "");
                GameTarget Pick(params string[] argv) => GameTarget.From(new Args(argv), 27275, null);

                // ---- 대상 고르기 ----
                GameTarget attached = Pick("run");
                Check(attached is AttachedTarget && !attached.CanLaunch && attached.Kind == "editor", "nothing given: attach to the game that is running");
                GameTarget pc = Pick("run", "--app", Path.Combine(folder, "missing.exe"));
                Check(pc is PcBuildTarget && pc.CanLaunch, "--app: a PC build the runner starts");
                try { pc.Launch(); Check(false, "a build that is not there cannot be started"); }
                catch (UsageException e) { Check(e.Message.StartsWith("no such file"), "and it says so when starting, not when attaching"); }
                GameTarget android = Pick("run", "--device", "emulator-5554", "--package", "com.game", "--adb", adb);
                Check(android is AndroidTarget && android.CanLaunch && android.Kind == "android", "--device and --package: an Android app the runner starts");
                GameTarget running = Pick("run", "--device", "emulator-5554", "--adb", adb);
                Check(running is AndroidTarget && !running.CanLaunch, "--device alone: attach to the app running on the device");
                Check(running.Stop("x.log", false) == null, "an attached target has nothing to stop");
                foreach (string[] bad in new[] { new[] { "run", "--app", "a.exe", "--package", "p" }, new[] { "run", "--package", "com.game" } })
                {
                    try { Pick(bad); Check(false, "a target that makes no sense is refused: " + string.Join(" ", bad)); }
                    catch (UsageException) { }
                }

                // ---- 결과 출력: 고르기 ----
                Environment.SetEnvironmentVariable("GITHUB_STEP_SUMMARY", null);
                Environment.SetEnvironmentVariable("GITHUB_OUTPUT", null);
                string reports = Path.Combine(folder, "reports");
                List<IRunOutput> outputs = RunOutputs.For(new Args(new[] { "run", "--reports", reports }), null, out RunEvidence evidence);
                Check(outputs.Count == 1 && outputs[0] is ReportOutput && evidence != null, "by default only the run report, and evidence is gathered for it");
                outputs = RunOutputs.For(new Args(new[] { "run", "--reports", "off" }), null, out evidence);
                Check(outputs.Count == 0 && evidence == null, "--reports off: no report and no evidence");
                Environment.SetEnvironmentVariable("GITHUB_STEP_SUMMARY", Path.Combine(folder, "summary.md"));
                var csv = new TcSheet(new SheetConfig(), new CsvSheet(new SheetConfig()));
                outputs = RunOutputs.For(new Args(new[] { "run", "--reports", reports, "--junit", Path.Combine(folder, "junit.xml") }), csv, out evidence);
                Check(outputs.ConvertAll(o => o.GetType().Name).ToArray().AsSpan().SequenceEqual(new[] { "ReportOutput", "SheetOutput", "JunitOutput", "GithubActionsOutput" }),
                    "all four in order: the report first, because the others use where it went");

                // ---- 결과 출력: 하나가 실패해도 나머지는 남는다 ----
                var log = new List<string>();
                var said = new StringWriter();
                Console.SetError(said);
                RunOutputs.WriteAll(new List<IRunOutput> { new Recorder("report", log, false), new Recorder("sheet", log, true), new Recorder("ci", log, false) }, new RunRecord());
                Check(string.Join(",", log) == "report:-,sheet:r1,ci:r1", "every output is written, in order, and later ones see what earlier ones left");
                Check(said.ToString().Contains("qaplay: sheet was not written: disk full"), "a failed output is reported");
            }
            finally
            {
                Console.SetError(error);
                Environment.SetEnvironmentVariable("GITHUB_STEP_SUMMARY", oldSummary);
                Environment.SetEnvironmentVariable("GITHUB_OUTPUT", oldOutput);
                Directory.Delete(folder, true);
            }
        }
    }
}
