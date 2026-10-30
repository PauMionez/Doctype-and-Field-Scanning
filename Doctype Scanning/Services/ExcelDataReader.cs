using Doctype_Scanning.Models;
using Syncfusion.XlsIO;
using System.IO;

namespace Doctype_Scanning.Services;

/// <summary>
/// Reads the "Data" sheet of an excel export of a db3: one row per record, the CI field labels as column headers,
/// header-field values repeated on every record row, and a blank SequenceNum for an image that has no record.
/// The export has no RecordID column, so errors in it are located by image name (and sequence number).
/// </summary>
internal static class ExcelDataReader
{
    private const string DataSheet = "Data";

    public static SourceSnapshot Read(string file, string sourceName, CiField field)
    {
        SourceSnapshot snapshot = new();

        using ExcelEngine engine = new();
        IApplication app = engine.Excel;
        app.DefaultVersion = ExcelVersion.Xlsx;

        // FileShare.ReadWrite so an export that is still open in excel can be read
        using FileStream stream = new(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        IWorkbook wb = app.Workbooks.Open(stream);

        IWorksheet ws = FindDataSheet(wb);
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
        int sequenceCol = columns.GetValueOrDefault("sequencenum");
        int sectionCol = columns.GetValueOrDefault("section");

        if (imageCol == 0 || docTypeCol == 0)
            throw new InvalidDataException($"The '{ws.Name}' sheet has no ImageName / DocType columns - not an export of a db3");

        // the export uses the CI field labels as headers; the field name is the fallback
        int fieldCol = columns.GetValueOrDefault(TextUtil.Loose(field.Label));
        if (fieldCol == 0) fieldCol = columns.GetValueOrDefault(TextUtil.Loose(field.Name));

        if (fieldCol == 0)
        {
            snapshot.FieldProblem = $"[Typo] Column '{field.Label}' was not found in the '{ws.Name}' sheet - the field was not checked";
        }

        snapshot.FieldIsHeader = field.IsHeader && fieldCol > 0;

        // SequenceNum marks the record rows of an export, but some excels keep that column empty
        // (e.g. a validation copy): then a row is a record when any of its keyed columns has a value
        bool sequenceFilled = sequenceCol > 0 && Enumerable.Range(2, Math.Max(0, lastRow - 1))
            .Any(r => !string.IsNullOrWhiteSpace(ws.Range[r, sequenceCol].DisplayText));

        int totalCharCol = columns.GetValueOrDefault("totalcharactercount");
        int[] keyedCols = columns.Values
            .Where(c => c != imageCol && c != docTypeCol && c != sectionCol && c != sequenceCol && c != totalCharCol && c != fieldCol)
            .ToArray();

        Dictionary<string, long> imageIds = new(StringComparer.Ordinal);
        HashSet<(long, string, string)> seenHeaderValues = new();

        for (int row = 2; row <= lastRow; row++)
        {
            string imageName = (ws.Range[row, imageCol].DisplayText ?? string.Empty).Trim();
            if (imageName.Length == 0) continue;

            if (!imageIds.TryGetValue(imageName, out long imageId))
            {
                imageId = imageIds.Count + 1;
                imageIds[imageName] = imageId;

                // raw text, so a doctype with a stray space is still caught
                string docType = ws.Range[row, docTypeCol].DisplayText ?? string.Empty;
                snapshot.Images.Add(new ImageRow(imageId, imageName, docType.Length == 0 ? null : docType));
            }

            string sequence = sequenceCol > 0 ? (ws.Range[row, sequenceCol].DisplayText ?? string.Empty).Trim() : string.Empty;

            bool isRecord = sequenceFilled
                ? sequence.Length > 0
                : keyedCols.Any(c => !string.IsNullOrWhiteSpace(ws.Range[row, c].DisplayText));

            if (isRecord)
            {
                snapshot.RecordStats.TryGetValue(imageId, out RecordStats stats);
                snapshot.RecordStats[imageId] = new RecordStats(stats.Count + 1, null);
            }

            if (fieldCol == 0) continue;

            string value = ws.Range[row, fieldCol].DisplayText ?? string.Empty;
            if (value.Length == 0) continue;

            if (field.IsHeader)
            {
                // header values repeat on every record row: keep one per image section
                string section = sectionCol > 0 ? (ws.Range[row, sectionCol].DisplayText ?? string.Empty).Trim() : string.Empty;
                if (!seenHeaderValues.Add((imageId, section, value))) continue;

                snapshot.HeaderValues.TryAdd(imageId, value);
                snapshot.Values.Add(new ValueEntry(sourceName, imageName, null, value));
            }
            else
            {
                string location = sequence.Length > 0 ? $" (sequence {sequence})" : string.Empty;
                snapshot.Values.Add(new ValueEntry(sourceName, imageName, null, value, location));
            }
        }

        return snapshot;
    }

    internal static IWorksheet FindDataSheet(IWorkbook wb)
    {
        foreach (IWorksheet sheet in wb.Worksheets)
        {
            if (sheet.Name.Trim().Equals(DataSheet, StringComparison.OrdinalIgnoreCase)) return sheet;
        }

        // an export without the standard sheet name: the first sheet that looks like a db3 export
        foreach (IWorksheet sheet in wb.Worksheets)
        {
            HashSet<string> headers = new(StringComparer.Ordinal);
            for (int col = 1; col <= Math.Min(sheet.UsedRange.LastColumn, 10); col++)
                headers.Add(TextUtil.Loose(sheet.Range[1, col].DisplayText ?? string.Empty));

            if (headers.Contains("imagename") && headers.Contains("doctype")) return sheet;
        }

        throw new InvalidDataException("No 'Data' sheet with ImageName and DocType columns was found - not an export of a db3");
    }
}
