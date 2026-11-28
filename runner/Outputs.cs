using System;
using System.Collections.Generic;
using System.IO;

namespace Qaplay.Runner
{
    /// <summary>실행 한 번의 결과. 결과를 남기는 곳(IRunOutput)들이 차례로 보고, 앞의 것이 남긴 것(리포트의 위치)을 뒤의 것이 쓴다.</summary>
    sealed class RunRecord
    {
        public List<Outcome> outcomes;
        /// <summary>게임이 hello로 알려 준 것(앱, 플랫폼, 화면, 기기). 게임에 닿지 못했으면 null일 수 없다: 그때는 실행이 먼저 끝난다.</summary>
        public Dictionary<string, object> hello;
        public string build, device;
        public double seconds;
        public DateTime started;
        /// <summary>실행 중에 모은 증거(실패한 화면, 게임 로그). 리포트를 남기지 않으면 null.</summary>
        public RunEvidence evidence;
        /// <summary>실행 리포트를 남겼으면 그 ID와 폴더. 시트의 리포트 칸, JUnit의 사본, CI의 출력이 쓴다.</summary>
        public string reportId, reportFolder;
    }

    /// <summary>
    /// 실행 결과를 남기는 곳 하나(전략): 실행 리포트 폴더, TC 시트, JUnit XML, CI의 작업 요약.
    /// 어떤 것을 쓸지는 RunOutputs.For가 명령의 값과 프로젝트 설정에서 정한다. 하나가 실패해도 나머지는 남기고, 종료 코드는 바꾸지 않는다.
    /// </summary>
    interface IRunOutput
    {
        /// <summary>남기지 못했을 때 사람에게 보여 줄 이름.</summary>
        string Name { get; }
        void Write(RunRecord run);
    }

    static class RunOutputs
    {
        /// <summary>
        /// 이번 실행이 결과를 남길 곳들. 순서가 있다: 리포트가 먼저다(뒤의 것이 리포트의 위치를 쓴다).
        /// 설정이 틀렸으면(보관 기한 등) 실행하기 전에 여기서 알린다. evidence는 리포트를 남길 때만 모은다.
        /// </summary>
        public static List<IRunOutput> For(Args args, TcSheet sheet, out RunEvidence evidence)
        {
            var outputs = new List<IRunOutput>();
            evidence = null;
            ReportStore reports = ReportStore.ForRun(args);
            if (reports != null)
            {
                outputs.Add(new ReportOutput(reports, Retention.Load(reports)));
                evidence = new RunEvidence();
            }
            if (sheet != null) outputs.Add(new SheetOutput(sheet));
            if (args.Get("junit", null) is string junit) outputs.Add(new JunitOutput(junit));
            if (GithubActions.Running) outputs.Add(new GithubActionsOutput());
            return outputs;
        }

        /// <summary>차례로 남긴다. 남기지 못한 것은 알리기만 한다: 실행 결과는 그대로이므로 종료 코드를 바꾸지 않는다.</summary>
        public static void WriteAll(List<IRunOutput> outputs, RunRecord run)
        {
            foreach (IRunOutput output in outputs)
            {
                try
                {
                    output.Write(run);
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is UsageException)
                {
                    Console.Error.WriteLine("qaplay: " + output.Name + " was not written: " + e.Message);
                }
            }
        }
    }

    /// <summary>실행 리포트 폴더(qaplay reports, 대시보드). 남긴 뒤 기한이 지난 것을 정리한다.</summary>
    sealed class ReportOutput : IRunOutput
    {
        readonly ReportStore m_Store;
        readonly Retention m_Retention;

        public ReportOutput(ReportStore store, Retention retention)
        {
            m_Store = store;
            m_Retention = retention;
        }

        public string Name => "the run report";

        public void Write(RunRecord run)
        {
            run.reportId = m_Store.Record(run.outcomes, run.hello, run.build, run.device, run.seconds, run.evidence, run.started);
            run.reportFolder = m_Store.FolderOf(run.reportId);
            Console.WriteLine("report: " + run.reportFolder);
            List<string> expired = m_Store.Clean(m_Retention, DateTime.UtcNow, true);
            if (expired.Count > 0) Console.WriteLine("report: " + ReportCommands.Counts(expired) + " past their retention removed");
        }
    }

    /// <summary>TC 시트(CSV, Excel, Google Sheets): TC마다의 최신 결과와 요약 한 줄.</summary>
    sealed class SheetOutput : IRunOutput
    {
        readonly TcSheet m_Sheet;

        public SheetOutput(TcSheet sheet)
        {
            m_Sheet = sheet;
        }

        public string Name => "the results for the sheet";

        public void Write(RunRecord run)
        {
            foreach (string note in m_Sheet.Record(run.outcomes, run.build, run.device, null, run.reportId)) Console.WriteLine(note);
        }
    }

    /// <summary>CI가 읽는 JUnit XML. 리포트를 남겼으면 그 폴더에도 사본을 둔다(CI가 폴더 하나만 올리면 된다).</summary>
    sealed class JunitOutput : IRunOutput
    {
        readonly string m_Path;

        public JunitOutput(string path)
        {
            m_Path = path;
        }

        public string Name => "the JUnit XML";

        public void Write(RunRecord run)
        {
            Junit.Write(m_Path, run.outcomes, run.hello, run.seconds);
            if (run.reportFolder != null) File.Copy(m_Path, Path.Combine(run.reportFolder, "junit.xml"), true);
        }
    }

    /// <summary>GitHub Actions의 작업 요약과 단계의 출력(report). Actions 안에서 돌 때만 쓴다.</summary>
    sealed class GithubActionsOutput : IRunOutput
    {
        public string Name => "the GitHub Actions summary";

        public void Write(RunRecord run) => GithubActions.Write(run.outcomes, run.build, run.device, run.reportFolder);
    }
}
