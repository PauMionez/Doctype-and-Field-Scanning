using Doctype_Scanning.Models;
using Syncfusion.XlsIO;

namespace Doctype_Scanning.Services;

public static class ExcelExporter
{
    // an excel sheet holds 1,048,576 rows; leave room for the header
    private const int MaxRowsPerSheet = 1_000_000;

    private static readonly string[] Headers = { "DB3", "Error Message", "Value", "Image Name", "Record ID" };

    public static void Export(string outputPath, IReadOnlyList<ErrorRecord> errors)
    {
        using ExcelEngine engine = new();
        IApplication app = engine.Excel;
        app.DefaultVersion = ExcelVersion.Xlsx;

        int sheetCount = Math.Max(1, (int)Math.Ceiling(errors.Count / (double)MaxRowsPerSheet));
        IWorkbook wb = app.Workbooks.Create(sheetCount);

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

        wb.SaveAs(outputPath);
        wb.Close();
    }
}
