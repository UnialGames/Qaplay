using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Qaplay.Runner
{
    /// <summary>Excel 파일 하나: TC 표와 요약 표가 탭으로 들어 있다. Excel 모듈(modules/xlsx)이 더하는 시트의 종류다.</summary>
    sealed class XlsxProvider : ISheetProvider
    {
        public string Type => "xlsx";
        public string Name => "Excel file (.xlsx)";

        public bool Fits(Args args) => args.Get("file", null) is string file && file.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase);

        public void Check(SheetConfig config)
        {
            if (config.File == null) throw new FormatException("an xlsx sheet needs \"file\": the workbook");
            if (config.Tc == null) throw new FormatException("\"tc\" is missing: the name of the tab with the test cases");
        }

        public SheetStore Open(SheetConfig config) => new XlsxSheet(config);

        public SheetSource Locate(SetupContext context)
        {
            string path = context.Need("file", "FILE.xlsx", "Path of the Excel file (it is created if it does not exist)");
            if (!path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)) throw new UsageException("an Excel sheet is a .xlsx file: " + path);
            var source = new SheetSource();
            source.settings["file"] = context.Stored(path);
            if (File.Exists(path)) source.tables.AddRange(XlsxSheet.Tables(path));
            // 새 파일이면 TC라는 탭을 만들게 된다
            if (source.tables.Count == 0) source.tables.Add(new SheetTable { name = "TC" });
            return source;
        }

        public string SummaryName(string tc, string language) => language == "ko" ? "요약" : "Summary";
    }

    /// <summary>
    /// Excel 파일 하나: TC 표와 요약 표가 탭으로 들어 있다. ClosedXML로 읽고 쓴다.
    /// 쓸 때는 글자가 바뀐 칸만 고친다. 사람이 적은 칸의 값과 서식(굵기, 색, 칸 너비)은 그대로 남는다.
    /// </summary>
    // ponytail: 파일을 통째로 읽어 다시 저장한다. ClosedXML이 다루지 못하는 것(일부 차트, 피벗, 매크로)이 든 파일은
    // 저장할 때 그 부분이 빠질 수 있다. TC 전용 파일을 쓰게 안내한다
    sealed class XlsxSheet : SheetStore
    {
        readonly SheetConfig m_Config;

        public XlsxSheet(SheetConfig config)
        {
            m_Config = config;
        }

        string FilePath => m_Config.PathOf(m_Config.File);
        string Tab(string which) => which == "tc" ? m_Config.Tc : m_Config.Summary;

        public override string Where(string which) => Tab(which) == null ? null : FilePath + " [" + Tab(which) + "]";

        ClosedXML.Excel.XLWorkbook Load() => Load(FilePath);

        // Excel이 열어 둔 파일도 읽을 수는 있게, 다른 프로그램의 쓰기를 허용하며 연다.
        // 파일은 메모리로 다 읽어 들인다: ClosedXML은 저장할 때 처음 읽은 흐름을 다시 쓰므로 그때까지 살아 있어야 한다
        static ClosedXML.Excel.XLWorkbook Load(string file)
        {
            var memory = new MemoryStream();
            using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) stream.CopyTo(memory);
            memory.Position = 0;
            return new ClosedXML.Excel.XLWorkbook(memory);
        }

        /// <summary>통합 문서의 탭들과 탭마다 첫 줄의 칸 이름. 준비 과정이 어느 탭이 TC 표인지 고르게 하는 데 쓴다.</summary>
        public static List<SheetTable> Tables(string file)
        {
            var tables = new List<SheetTable>();
            using ClosedXML.Excel.XLWorkbook workbook = Load(file);
            foreach (ClosedXML.Excel.IXLWorksheet sheet in workbook.Worksheets)
            {
                var table = new SheetTable { name = sheet.Name };
                int last = sheet.Row(1).LastCellUsed()?.Address.ColumnNumber ?? 0;
                for (int c = 1; c <= last; c++) table.header.Add(Text(sheet.Cell(1, c)));
                tables.Add(table);
            }
            return tables;
        }

        public override List<List<string>> Read(string which)
        {
            var rows = new List<List<string>>();
            if (Tab(which) == null || !System.IO.File.Exists(FilePath)) return rows;
            using ClosedXML.Excel.XLWorkbook workbook = Load();
            if (!workbook.TryGetWorksheet(Tab(which), out ClosedXML.Excel.IXLWorksheet sheet)) return rows;
            int lastRow = sheet.LastRowUsed()?.RowNumber() ?? 0, lastColumn = sheet.LastColumnUsed()?.ColumnNumber() ?? 0;
            for (int r = 1; r <= lastRow; r++)
            {
                var row = new List<string>();
                // 화면에 보이는 글자로 읽는다(날짜나 숫자 서식이 적용된 모습)
                for (int c = 1; c <= lastColumn; c++) row.Add(Text(sheet.Cell(r, c)));
                rows.Add(row);
            }
            return rows;
        }

        // 수식은 계산된 값으로 읽는다. Excel이 저장한 파일에는 그 값이 들어 있지만, 다른 도구가 만든 파일에는 없을 수 있어 그때는 계산한다.
        // 계산하지 못하는 수식(지원하지 않는 함수 등)은 들어 있던 값 그대로 둔다
        static string Text(ClosedXML.Excel.IXLCell cell)
        {
            if (cell.HasFormula && cell.CachedValue.IsBlank)
                try { cell.InvalidateFormula(); _ = cell.Value; } catch (Exception) { }
            return cell.GetFormattedString();
        }

        public override void Write(string which, List<List<string>> rows)
        {
            bool exists = System.IO.File.Exists(FilePath);
            using ClosedXML.Excel.XLWorkbook workbook = exists ? Load() : new ClosedXML.Excel.XLWorkbook();
            if (!workbook.TryGetWorksheet(Tab(which), out ClosedXML.Excel.IXLWorksheet sheet)) sheet = workbook.AddWorksheet(Tab(which));
            for (int r = 0; r < rows.Count; r++)
            for (int c = 0; c < rows[r].Count; c++)
            {
                ClosedXML.Excel.IXLCell cell = sheet.Cell(r + 1, c + 1);
                string text = rows[r][c] ?? "";
                // 보이는 글자가 같으면 건드리지 않는다. 사람이 적은 칸은 값의 종류(숫자, 날짜)와 서식까지 그대로 남는다
                if (Text(cell) == text) continue;
                // 개수 같은 것은 Excel에서 셈할 수 있게 숫자로 적는다. 0으로 시작하는 글자(번호)는 글자로 둔다
                if (text.Length > 0 && text.Length < 10 && (text == "0" || text[0] != '0') && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int number))
                    cell.Value = number;
                else cell.Value = text;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            // 옆에 다 쓴 뒤에 바꿔치기한다: 쓰다가 죽어도 사람의 통합 문서가 반쯤 쓰인 채로 남지 않는다
            string temp = Path.ChangeExtension(FilePath, ".qaplay-tmp" + Path.GetExtension(FilePath));
            try
            {
                workbook.SaveAs(temp);
                System.IO.File.Move(temp, FilePath, true);
            }
            catch (UnauthorizedAccessException)
            {
                // Excel이 열어 둔 파일은 바꿔치기가 거부된다. 원본은 그대로다
                throw new IOException("cannot write " + FilePath + ": it is open in another program (Excel?) or read-only");
            }
            finally
            {
                System.IO.File.Delete(temp);
            }
        }
    }

}
