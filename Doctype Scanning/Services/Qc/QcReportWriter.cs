using Doctype_Scanning.Models;
using Syncfusion.XlsIO;
using System.Globalization;

namespace Doctype_Scanning.Services.Qc;

/// <summary>
/// Writes the QC report of one batch: the flags with empty reviewer columns, and a summary
/// (flags per check, images per DocType). The batch is held until every flag has a reviewer decision.
/// </summary>
public static class QcReportWriter
{
    public const string ErrorDecision = "Error";
    public const string NotAnErrorDecision = "Not an error";

    private static readonly string[] Headers =
    {
        "ImageName", "Field", "Keyed Value", "Check No.", "Message", "Suggested Value",
        "Reviewer Decision", "Corrected Value", "Reviewer", "Date"
    };

    private static readonly int[] Widths = { 34, 24, 34, 11, 90, 32, 20, 26, 18, 14 };

    public static void Write(string outputPath, QcResult result)
    {
        using ExcelEngine engine = new();
        IApplication app = engine.Excel;
        app.DefaultVersion = ExcelVersion.Xlsx;

        IWorkbook wb = app.Workbooks.Create(2);
        WriteFlags(wb.Worksheets[0], result);
        WriteSummary(wb.Worksheets[1], result);

        wb.SaveAs(outputPath);
        wb.Close();
    }

    private static void WriteFlags(IWorksheet ws, QcResult result)
    {
        ws.Name = "QC Flags";

        for (int col = 0; col < Headers.Length; col++)
            ws.Range[1, col + 1].Text = Headers[col];

        if (result.Flags.Count > 0)
        {
            object[,] data = new object[result.Flags.Count, Headers.Length];
            for (int i = 0; i < result.Flags.Count; i++)
            {
                QcFlag f = result.Flags[i];
                data[i, 0] = f.ImageName;
                data[i, 1] = f.Field;
                data[i, 2] = f.KeyedValue;
                data[i, 3] = f.Check;
                data[i, 4] = f.Message;
                data[i, 5] = f.Suggested;

                // reviewer columns stay empty: the reviewer fills them in
                data[i, 6] = string.Empty;
                data[i, 7] = string.Empty;
                data[i, 8] = string.Empty;
                data[i, 9] = string.Empty;
            }

            ws.ImportArray(data, 2, 1);

            IDataValidation decision = ws.Range[2, 7, result.Flags.Count + 1, 7].DataValidation;
            decision.AllowType = ExcelDataType.User;
            decision.ListOfValues = new[] { ErrorDecision, NotAnErrorDecision };
        }

        StyleHeader(ws.Range[1, 1, 1, Headers.Length]);

        // the reviewer columns look different so it is clear what is for them
        ws.Range[1, 7, 1, 10].CellStyle.Color = System.Drawing.Color.FromArgb(0x7F, 0x60, 0x00);

        for (int col = 0; col < Widths.Length; col++)
            ws.SetColumnWidth(col + 1, Widths[col]);

        ws.Range["A2"].FreezePanes();
        ws.AutoFilters.FilterRange = ws.Range[1, 1, Math.Max(2, result.Flags.Count + 1), Headers.Length];
    }

    private static void WriteSummary(IWorksheet ws, QcResult result)
    {
        ws.Name = "Summary";
        int row = 1;

        ws.Range[row, 1].Text = "Pre-delivery QC - 63564.IDX.001";
        ws.Range[row, 1].CellStyle.Font.Bold = true;
        ws.Range[row, 1].CellStyle.Font.Size = 14;
        row += 2;

        ws.Range[row, 1].Text = "Batch";
        ws.Range[row++, 2].Text = result.BatchName;
        ws.Range[row, 1].Text = "Images";
        ws.Range[row++, 2].Number = result.ImageCount;
        ws.Range[row, 1].Text = "Flags";
        ws.Range[row++, 2].Number = result.Flags.Count;
        ws.Range[row, 1].Text = "Capture limit";
        ws.Range[row++, 2].Text = $"{result.CaptureLimitPercent.ToString("0.#", CultureInfo.InvariantCulture)}%";
        ws.Range[row, 1].Text = "Generated";
        ws.Range[row++, 2].Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        ws.Range[row, 1].Text = "Hold the batch until every flag on the 'QC Flags' sheet has a Reviewer Decision.";
        ws.Range[row, 1].CellStyle.Font.Italic = true;
        row += 2;

        // flags per check (every check is listed, also with 0)
        ws.Range[row, 1].Text = "Check No.";
        ws.Range[row, 2].Text = "Check";
        ws.Range[row, 3].Text = "Flags";
        ws.Range[row, 4].Text = "Images flagged";
        StyleHeader(ws.Range[row, 1, row, 4]);
        row++;

        foreach ((int check, string name) in QcRunner.CheckNames)
        {
            List<QcFlag> flags = result.Flags.Where(f => f.Check == check).ToList();

            ws.Range[row, 1].Number = check;
            ws.Range[row, 2].Text = check == QcRunner.DictionaryCheck && !result.DictionaryRun ? $"{name} - not run" : name;
            ws.Range[row, 3].Number = flags.Count;
            ws.Range[row, 4].Number = flags.Where(f => f.ImageName.Length > 0).Select(f => f.ImageName).Distinct().Count();
            row++;
        }

        row++;

        // images per DocType
        ws.Range[row, 1].Text = "DocType";
        ws.Range[row, 2].Text = "Images";
        StyleHeader(ws.Range[row, 1, row, 2]);
        row++;

        foreach ((string doctype, int count) in result.DocTypeCounts)
        {
            ws.Range[row, 1].Text = doctype;
            ws.Range[row, 2].Number = count;
            row++;
        }

        if (result.Notes.Count > 0)
        {
            row++;
            ws.Range[row, 1].Text = "Notes";
            ws.Range[row, 1].CellStyle.Font.Bold = true;
            row++;

            foreach (string note in result.Notes)
                ws.Range[row++, 1].Text = note;
        }

        ws.SetColumnWidth(1, 22);
        ws.SetColumnWidth(2, 52);
        ws.SetColumnWidth(3, 12);
        ws.SetColumnWidth(4, 16);
    }

    private static void StyleHeader(IRange header)
    {
        header.CellStyle.Font.Bold = true;
        header.CellStyle.Color = System.Drawing.Color.FromArgb(0x1F, 0x4E, 0x79);
        header.CellStyle.Font.Color = ExcelKnownColors.White;
    }
}
