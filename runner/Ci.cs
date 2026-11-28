using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Qaplay.Runner
{
    /// <summary>
    /// GitHub Actions 안에서 run을 돌렸을 때 남기는 것. Actions가 스텝마다 주는 환경 변수로 안다(밖에서는 아무것도 하지 않는다).
    /// 작업 요약(GITHUB_STEP_SUMMARY)에는 규칙마다의 결과를 표로, 스텝의 출력(GITHUB_OUTPUT)에는 이번 실행의 리포트 폴더를 report로 적는다.
    /// 워크플로가 러너의 출력을 읽어 해석하지 않아도 되게 하려는 것이다.
    /// </summary>
    static class GithubActions
    {
        /// <summary>GitHub Actions 안에서 도는지: 요약이나 출력을 적을 파일을 Actions가 알려 줬는지.</summary>
        public static bool Running =>
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY")) || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GITHUB_OUTPUT"));

        public static void Write(List<Outcome> outcomes, string build, string device, string reportFolder)
        {
            string summary = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY"), output = Environment.GetEnvironmentVariable("GITHUB_OUTPUT");
            if (!string.IsNullOrEmpty(summary)) File.AppendAllText(summary, Summary(outcomes, build, device), new UTF8Encoding(false));
            if (!string.IsNullOrEmpty(output) && reportFolder != null) File.AppendAllText(output, "report=" + reportFolder + "\n", new UTF8Encoding(false));
        }

        /// <summary>작업 요약에 붙일 Markdown: 한 줄 결과, 규칙마다의 표, 실패한 규칙의 내용.</summary>
        public static string Summary(List<Outcome> outcomes, string build, string device)
        {
            int passed = outcomes.FindAll(o => o.Status == "passed").Count, flaky = outcomes.FindAll(o => o.Status == "flaky").Count;
            var md = new StringBuilder();
            md.Append("### qaplay: ").Append(passed).Append(" passed, ").Append(flaky).Append(" flaky, ").Append(outcomes.Count - passed - flaky).Append(" failed\n\n");
            md.Append('`').Append(Code(build)).Append("` on `").Append(Code(device)).Append("`\n\n");
            md.Append("| Result | Rule | TC | Bug | Time |\n|---|---|---|---|---|\n");
            foreach (Outcome outcome in outcomes)
                md.Append("| ").Append(Mark(outcome)).Append(" | ").Append(Cell(Name(outcome))).Append(" | ").Append(Cell(outcome.rule?.tc)).Append(" | ")
                    .Append(outcome.bugId == null ? "" : "BUG-" + outcome.bugId).Append(" | ")
                    .Append(outcome.seconds.ToString("0.0", CultureInfo.InvariantCulture)).Append("s |\n");
            foreach (Outcome outcome in outcomes)
            {
                if (outcome.failures.Count == 0) continue;
                md.Append("\n<details><summary>").Append(Mark(outcome)).Append(": ").Append(Cell(Name(outcome))).Append("</summary>\n\n```text\n");
                for (int n = 0; n < outcome.failures.Count; n++)
                    // 실패 내용은 게임과 규칙 파일에서 온 글자다. 코드 블록 안에 가두고, 블록을 닫는 표시는 바꿔 적는다
                    md.Append("attempt ").Append(n + 1).Append(": ").Append(outcome.failures[n].Replace("```", "'''")).Append('\n');
                md.Append("```\n\n</details>\n");
            }
            return md.Append('\n').ToString();
        }

        static string Mark(Outcome outcome) => outcome.Status == "passed" ? "✅ passed" : outcome.Status == "flaky" ? "⚠️ flaky" : "❌ failed";

        static string Name(Outcome outcome) => outcome.rule?.name == null ? Path.GetFileName(outcome.file) : outcome.rule.name + " (" + Path.GetFileName(outcome.file) + ")";

        // 표의 칸: 칸을 나누는 글자와 줄바꿈, 꺾쇠를 무해하게 한다
        static string Cell(string text) =>
            (text ?? "").Replace("\\", "\\\\").Replace("|", "\\|").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\r", "").Replace("\n", " ");

        static string Code(string text) => (text ?? "").Replace("`", "'").Replace("\n", " ");
    }
}
