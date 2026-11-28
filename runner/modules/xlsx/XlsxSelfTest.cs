using System;
using System.Collections.Generic;
using System.IO;

namespace Qaplay.Runner
{
    /// <summary>Excel 모듈의 검사. qaplay selftest가 이 모듈이 설치되어 있으면 부른다.</summary>
    sealed class XlsxSelfTest : IModuleSelfTest
    {
        // 코어의 CSV와 이 모듈의 Excel
        static readonly SheetProviders Providers = new SheetProviders(new CsvProvider(), new XlsxProvider());

        static void Check(bool condition, string what)
        {
            if (!condition) throw new InvalidOperationException("selftest failed: " + what);
        }

        public void Run()
        {
            Workbook();
            Setup();
        }

        static void Workbook()
        {
            string folder = Path.Combine(Path.GetTempPath(), "qaplay-selftest-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(folder, "rules"));
                File.WriteAllText(Path.Combine(folder, "rules", "buy.yaml"), "tc: TC-1\n---\n- tapOn: 구매\n");
                string file = Path.Combine(folder, "qa.xlsx"), settings = Path.Combine(folder, "sheet.json");
                using (var workbook = new ClosedXML.Excel.XLWorkbook())
                {
                    ClosedXML.Excel.IXLWorksheet tc = workbook.AddWorksheet("TC 목록");
                    string[] header = { "번호", "내용", "기대 결과", "우선순위", "작성일" };
                    for (int c = 0; c < header.Length; c++) tc.Cell(1, c + 1).Value = header[c];
                    tc.Row(1).Style.Font.Bold = true;
                    tc.Cell(1, 1).Style.Fill.BackgroundColor = ClosedXML.Excel.XLColor.Yellow;
                    tc.Column(2).Width = 40;
                    tc.Cell(2, 1).Value = "TC-1";
                    tc.Cell(2, 2).Value = "구매를 누른다";
                    tc.Cell(2, 3).Value = "골드가 100 준다";
                    tc.Cell(2, 4).Value = 3;                             // 숫자로 적은 칸
                    tc.Cell(2, 5).Value = new DateTime(2026, 11, 1);     // 날짜로 적은 칸
                    tc.Cell(3, 1).Value = "TC-2";
                    tc.Cell(3, 2).Value = "판매를 누른다";
                    tc.Cell(3, 3).FormulaA1 = "\"골드가 \" & 50*2 & \" 는다\"";   // 수식. 계산된 값은 파일에 들어가지 않는다
                    workbook.AddWorksheet("메모").Cell(1, 1).Value = "도구가 건드리지 않는 탭";
                    workbook.SaveAs(file);
                }
                File.WriteAllText(settings,
                    "{\"rules\":[\"rules\"],\"sheet\":{\"type\":\"xlsx\",\"file\":\"qa.xlsx\",\"tc\":\"TC 목록\",\"summary\":\"요약\"," +
                    "\"columns\":{\"tc\":\"번호\",\"content\":\"내용\",\"expected\":\"기대 결과\",\"result\":\"결과\"},\"labels\":{\"passed\":\"통과\"}}}");
                SheetConfig config = SheetConfig.Load(settings, Providers);

                Check(Providers.Sheet(config).List().Count == 2, "test cases are read from the tab");
                Check(Providers.Sheet(config).List()[1].cells["기대 결과"].Equals("골드가 100 는다"),"a formula is read as its value");
                var ran = new Outcome { file = "buy.yaml", rule = RuleReader.Read("tc: TC-1\n---\n- tapOn: a\n"), passed = true };
                Providers.Sheet(config).Record(new List<Outcome> { ran }, "com.game 1.2", "Android");
                Providers.Sheet(config).Record(new List<Outcome> { ran }, "com.game 1.3", "Android");

                using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read))
                using (var workbook = new ClosedXML.Excel.XLWorkbook(stream))
                {
                    ClosedXML.Excel.IXLWorksheet tc = workbook.Worksheet("TC 목록");
                    Check(tc.Cell(1, 6).GetString() == "Rule" && tc.Cell(1, 7).GetString() == "결과", "tool columns are added after the human ones");
                    Check(tc.Cell(2, 6).GetString() == "ok" && tc.Cell(2, 7).GetString() == "통과", "rule status and latest result");
                    Check(tc.Cell(2, 8).GetString().StartsWith("com.game 1.3 / Android / "), "the latest run replaces the one before");
                    Check(tc.Cell(3, 6).GetString() == "missing", "a TC without a rule is marked");
                    Check(tc.Cell(1, 1).Style.Font.Bold && tc.Cell(1, 1).Style.Fill.BackgroundColor == ClosedXML.Excel.XLColor.Yellow
                          && Math.Abs(tc.Column(2).Width - 40) < 0.01, "the formatting people made stays");
                    Check(tc.Cell(2, 4).DataType == ClosedXML.Excel.XLDataType.Number && tc.Cell(2, 4).GetValue<int>() == 3
                          && tc.Cell(2, 5).DataType == ClosedXML.Excel.XLDataType.DateTime && tc.Cell(3, 3).HasFormula, "human cells keep their kind of value");
                    Check(workbook.Worksheet("메모").Cell(1, 1).GetString() == "도구가 건드리지 않는 탭", "other tabs stay");

                    ClosedXML.Excel.IXLWorksheet summary = workbook.Worksheet("요약");
                    Check(summary.Cell(1, 1).GetString() == "Build" && summary.Cell(3, 1).GetString() == "com.game 1.3" && summary.LastRowUsed().RowNumber() == 3,
                        "the summary tab is created and gets a row per run");
                    Check(summary.Cell(2, 4).DataType == ClosedXML.Excel.XLDataType.Number && summary.Cell(2, 8).GetString() == "100%", "counts are numbers");
                }

                // Excel이 열어 둔 것처럼 다른 쪽이 잡고 있으면: 읽기는 되고, 쓰기는 알리고 끝나며, 원본과 폴더는 그대로다
                byte[] before = File.ReadAllBytes(file);
                using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
                {
                    Check(Providers.Sheet(config).List().Count == 2, "a workbook open elsewhere can still be read");
                    try { Providers.Sheet(config).Record(null, null, null); Check(false, "writing to a workbook open elsewhere is refused"); }
                    catch (IOException) { }
                }
                Check(File.ReadAllBytes(file).AsSpan().SequenceEqual(before) && Directory.GetFiles(folder, "*.xlsx").Length == 1,
                    "a refused write leaves the workbook as it was and no temporary file");

                File.WriteAllText(settings, "{\"sheet\":{\"type\":\"xlsx\",\"tc\":\"TC 목록\"}}");
                try { SheetConfig.Load(settings, Providers); Check(false, "an xlsx sheet without a file is refused"); }
                catch (UsageException) { }
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }

        // 준비(tc init, tc tabs)가 코어의 CSV와 같은 길로 된다
        static void Setup()
        {
            string folder = Path.Combine(Path.GetTempPath(), "qaplay-selftest-" + Guid.NewGuid().ToString("N"));
            string was = Directory.GetCurrentDirectory();
            var said = new List<string>();
            try
            {
                Directory.CreateDirectory(Path.Combine(folder, "rules"));
                File.WriteAllText(Path.Combine(folder, "rules", "buy.yaml"), "tc: TC-1\n---\n- tapOn: 구매\n");
                Directory.SetCurrentDirectory(folder);
                SheetProviders providers = Providers;
                var ran = new Outcome { file = "buy.yaml", rule = RuleReader.Read("tc: TC-1\n---\n- tapOn: a\n"), passed = true };

                // ---- Excel: 없는 파일을 가리키면 만들어 준다 ----
                Check(SheetSetup.Run(new Args(new[] { "tc", "init", "--settings", "excel.json", "--type", "xlsx", "--file", "qa/cases.xlsx", "--lang", "ko", "--rules", "rules" }),
                    providers, question => null, said.Add) == 0, "an Excel sheet is set up with options only");
                SheetConfig excel = SheetConfig.Load("excel.json", providers);
                Check(excel.Type == "xlsx" && excel.File == "qa/cases.xlsx" && excel.Tc == "TC" && excel.Summary == "요약" && excel.Rules[0] == "rules"
                      && excel.Column("tc") == "TC 번호", "the settings keep paths relative to the settings file");
                List<SheetTable> made = XlsxSheet.Tables(Path.Combine(folder, "qa", "cases.xlsx"));
                Check(made.Count == 1 && made[0].name == "TC" && made[0].header[0] == "TC 번호" && made[0].header[3] == "규칙 상태", "the workbook is created with the column names");
                // 사람이 TC를 적은 뒤: 다른 탭이 생겨도 설정은 그대로 돈다
                var writer = providers.Get("xlsx").Open(excel);
                List<List<string>> rows = writer.Read("tc");
                rows.Add(new List<string> { "TC-1", "구매를 누른다", "골드가 100 준다" });
                writer.Write("tc", rows);
                providers.Sheet(excel).Record(new List<Outcome> { ran }, "1.2", "PC");
                TcRow first = providers.Sheet(excel).List()[0];
                Check(first.id == "TC-1" && first.ruleStatus == "ok" && first.cells["최신 결과"].Equals("통과"), "results are written to the Excel sheet");
                Console.SetOut(TextWriter.Null);
                try
                {
                    Check(SheetSetup.TabsCommand(new Args(new[] { "tc", "tabs", "--file", "qa/cases.xlsx" }), providers) == 0, "the tabs of a workbook can be listed (kind taken from the file name)");
                }
                finally
                {
                    Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
                }

                File.WriteAllText("cases.csv", "ID,Steps\r\nTC-1,Press buy\r\n");
                try
                {
                    SheetSetup.Run(new Args(new[] { "tc", "init", "--settings", "bad.json", "--type", "xlsx", "--file", "cases.csv" }), providers, question => null, said.Add);
                    Check(false, "a file of the wrong kind is refused");
                }
                catch (UsageException) { }
            }
            finally
            {
                Directory.SetCurrentDirectory(was);
                Directory.Delete(folder, true);
            }
        }
    }
}
