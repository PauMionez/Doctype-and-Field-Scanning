using Doctype_Scanning.Models;
using Syncfusion.XlsIO;
using System.IO;

namespace Doctype_Scanning.Services.Qc;

/// <summary>Reads the "Data" sheet of a db3 excel export for the QC (same layout as <see cref="ExcelDataReader"/>).</summary>
internal static class QcExcelReader
{
    public static QcBatch Read(string file, string batchName, QcFieldMap map)
    {
        QcBatch batch = new() { Name = batchName };

        using ExcelEngine engine = new();
        IApplication app = engine.Excel;
        app.DefaultVersion = ExcelVersion.Xlsx;

        using FileStream stream = new(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        IWorkbook wb = app.Workbooks.Open(stream);
        IWorksheet ws = ExcelDataReader.FindDataSheet(wb);

        int lastRow = ws.UsedRange.LastRow;
        int lastCol = ws.UsedRange.LastColumn;

        Dictionary<string, int> columns = new(StringComparer.Ordinal);
        for (int col = 1; col <= lastCol; col++)
        {
            string header = TextUtil.Loose(ws.Range[1, col].DisplayText ?? string.Empty);
            if (header.Length > 0) columns.TryAdd(header, col);
        }

        int imageCol = columns.GetValueOrDefault("imagename");
        int docTypeCol = columns.GetValueOrDefault("doctype");
        int sectionCol = columns.GetValueOrDefault("section");
        int sequenceCol = columns.GetValueOrDefault("sequencenum");
        int totalCharCol = columns.GetValueOrDefault("totalcharactercount");

        if (imageCol == 0 || docTypeCol == 0)
            throw new InvalidDataException("No ImageName / DocType columns - not an export of a db3");

        // the excel uses the CI field labels as headers; the field name is the fallback
        int ColumnOf(string label)
        {
            int col = columns.GetValueOrDefault(TextUtil.Loose(label));
            CiField? field = map.Get(label);
            return col != 0 || field == null ? col : columns.GetValueOrDefault(TextUtil.Loose(field.Name));
        }

        Dictionary<string, int> headerCols = QcFields.Header.Select(l => (l, c: ColumnOf(l))).Where(x => x.c > 0).ToDictionary(x => x.l, x => x.c);
        Dictionary<string, int> recordCols = QcFields.Record.Select(l => (l, c: ColumnOf(l))).Where(x => x.c > 0).ToDictionary(x => x.l, x => x.c);

        foreach (string label in QcFields.Header.Concat(QcFields.Record))
        {
            if (!headerCols.ContainsKey(label) && !recordCols.ContainsKey(label) && map.Get(label) != null)
                batch.Notes.Add($"Column '{label}' was not found in the excel - its checks were skipped.");
        }

        // every column that carries keyed data, for the "Capture with keyed data" check
        int[] keyedCols = columns.Values
            .Where(c => c != imageCol && c != docTypeCol && c != sectionCol && c != sequenceCol && c != totalCharCol)
            .ToArray();

        // columns that make a row a record when SequenceNum is not filled in (header values repeat on every row, so they do not count)
        int[] recordMarkCols = keyedCols.Where(c => !headerCols.ContainsValue(c)).ToArray();

        bool sequenceFilled = sequenceCol > 0 && Enumerable.Range(2, Math.Max(0, lastRow - 1))
            .Any(r => !string.IsNullOrWhiteSpace(ws.Range[r, sequenceCol].DisplayText));

        Dictionary<string, QcImage> images = new(StringComparer.Ordinal);

        for (int row = 2; row <= lastRow; row++)
        {
            string name = (ws.Range[row, imageCol].DisplayText ?? string.Empty).Trim();
            if (name.Length == 0) continue;

            if (!images.TryGetValue(name, out QcImage? image))
            {
                image = new QcImage { Name = name, DocType = (ws.Range[row, docTypeCol].DisplayText ?? string.Empty).Trim() };
                images[name] = image;
                batch.Images.Add(image);
            }

            string section = sectionCol > 0 ? (ws.Range[row, sectionCol].DisplayText ?? string.Empty).Trim() : string.Empty;

            foreach ((string label, int col) in headerCols)
                image.AddHeader(label, section, ws.Range[row, col].DisplayText ?? string.Empty);

            bool isRecord = sequenceFilled
                ? !string.IsNullOrWhiteSpace(ws.Range[row, sequenceCol].DisplayText)
                : recordMarkCols.Any(c => !string.IsNullOrWhiteSpace(ws.Range[row, c].DisplayText));

            if (isRecord) image.RecordCount++;

            foreach ((string label, int col) in recordCols)
            {
                string value = ws.Range[row, col].DisplayText ?? string.Empty;
                if (value.Trim().Length > 0) image.RecordValues.TryAdd(label, value);
            }

            if (!image.HasKeyedData)
                image.HasKeyedData = isRecord || keyedCols.Any(c => !string.IsNullOrWhiteSpace(ws.Range[row, c].DisplayText));
        }

        return batch;
    }
}
