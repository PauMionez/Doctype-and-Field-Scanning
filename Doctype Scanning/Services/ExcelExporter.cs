using Doctype_Scanning.Models;
using Syncfusion.XlsIO;

namespace Doctype_Scanning.Services;

public static class ExcelExporter
{
    // an excel sheet holds 1,048,576 rows; leave room for the header
    private const int MaxRowsPerSheet = 1_000_000;

    private static readonly string[] Headers = { "DB3", "Error Message", "Value", "Image Name", "Record ID" };

    /// <param name="notes">Comments (the most common value is not in the dictionary); written to their own sheet when there are any.</param>
    public static void Export(string outputPath, IReadOnlyList<ErrorRecord> errors, IReadOnlyList<DictionaryNote>? notes = null)
    {
        using ExcelEngine engine = new();
        IApplication app = engine.Excel;
        app.DefaultVersion = ExcelVersion.Xlsx;

        int sheetCount = Math.Max(1, (int)Math.Ceiling(errors.Count / (double)MaxRowsPerSheet));
        bool hasNotes = notes is { Count: > 0 };
        IWorkbook wb = app.Workbooks.Create(sheetCount + (hasNotes ? 1 : 0));

        for (int s = 0; s < sheetCount; s++)
        {
            IWorksheet ws = wb.Worksheets[s];
            ws.Name = s == 0 ? "Errors" : $"Errors {s + 1}";

            int count = Math.Min(MaxRowsPerSheet, errors.Count - s * MaxRowsPerSheet);

            for (int col = 0; col < Headers.Length; col++)
                ws.Range[1, col + 1].Text = Headers[col];

            if (count > 0)
            {
                object[,] data = new object[count, Headers.Length];
                for (int i = 0; i < count; i++)
                {
                    ErrorRecord e = errors[s * MaxRowsPerSheet + i];
                    data[i, 0] = e.Db3;
                    data[i, 1] = e.Message;
                    data[i, 2] = e.Value;
                    data[i, 3] = e.ImageName;
                    data[i, 4] = e.RecordId.HasValue ? e.RecordId.Value : string.Empty;
                }

                ws.ImportArray(data, 2, 1);
            }

            IRange header = ws.Range[1, 1, 1, Headers.Length];
            header.CellStyle.Font.Bold = true;
            header.CellStyle.Color = System.Drawing.Color.FromArgb(0x1F, 0x4E, 0x79);
            header.CellStyle.Font.Color = ExcelKnownColors.White;

            // fixed widths: autofitting a column of up to a million rows is slow
            ws.SetColumnWidth(1, 48);
            ws.SetColumnWidth(2, 85);
            ws.SetColumnWidth(3, 32);
            ws.SetColumnWidth(4, 40);
            ws.SetColumnWidth(5, 14);

            ws.Range["A2"].FreezePanes();
            ws.AutoFilters.FilterRange = ws.Range[1, 1, count + 1, Headers.Length];
        }

        if (hasNotes) WriteNotes(wb.Worksheets[sheetCount], notes!);

        wb.SaveAs(outputPath);
        wb.Close();
    }

    /// <summary>The comments sheet: values that are the most common but not in the dictionary. They are not errors.</summary>
    private static void WriteNotes(IWorksheet ws, IReadOnlyList<DictionaryNote> notes)
    {
        ws.Name = "Dictionary Notes";

        string[] headers = { "Field", "Value", "Count", "Of", "Dictionary", "Comment" };
        for (int col = 0; col < headers.Length; col++)
            ws.Range[1, col + 1].Text = headers[col];

        for (int i = 0; i < notes.Count; i++)
        {
            DictionaryNote n = notes[i];
            ws.Range[i + 2, 1].Text = n.Field;
            ws.Range[i + 2, 2].Text = n.Value;
            ws.Range[i + 2, 3].Number = n.Count;
            ws.Range[i + 2, 4].Number = n.Total;
            ws.Range[i + 2, 5].Text = n.Dictionary;
            ws.Range[i + 2, 6].Text = n.Message;
        }

        IRange header = ws.Range[1, 1, 1, headers.Length];
        header.CellStyle.Font.Bold = true;
        header.CellStyle.Color = System.Drawing.Color.FromArgb(0x1F, 0x4E, 0x79);
        header.CellStyle.Font.Color = ExcelKnownColors.White;

        int[] widths = { 22, 34, 9, 9, 42, 90 };
        for (int col = 0; col < widths.Length; col++)
            ws.SetColumnWidth(col + 1, widths[col]);
    }
}
