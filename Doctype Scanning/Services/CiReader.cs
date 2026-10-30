using Doctype_Scanning.Models;
using Syncfusion.XlsIO;
using System.IO;
using System.Text.RegularExpressions;

namespace Doctype_Scanning.Services;

/// <summary>
/// Reads the "Doctypes" and "Project Fields" sections of the "Project Requirements" sheet of a CI excel.
/// Columns are found by their header text because the CI layout differs from project to project
/// (Key / Keyed / Extracted, extra columns, ...).
/// </summary>
public static class CiReader
{
    private const string RequirementsSheet = "Project Requirements";

    public static CiData Read(string ciFile)
    {
        using ExcelEngine engine = new();
        IApplication app = engine.Excel;
        app.DefaultVersion = ExcelVersion.Xlsx;

        // FileShare.ReadWrite so a CI that is still open in excel can be imported
        using FileStream stream = new(ciFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        IWorkbook wb = app.Workbooks.Open(stream);

        IWorksheet? ws = null;
        foreach (IWorksheet sheet in wb.Worksheets)
        {
            if (sheet.Name.Trim().Equals(RequirementsSheet, StringComparison.OrdinalIgnoreCase))
            {
                ws = sheet;
                break;
            }
        }

        if (ws == null)
            throw new InvalidDataException($"Worksheet '{RequirementsSheet}' was not found in the CI. It is required to read the Doctypes and Project Fields.");

        int lastRow = ws.UsedRange.LastRow;
        int lastCol = ws.UsedRange.LastColumn;

        int doctypesRow = FindSectionRow(ws, lastRow, "Doctypes");
        int fieldsRow = FindSectionRow(ws, lastRow, "Project Fields");

        if (doctypesRow == 0)
            throw new InvalidDataException("The 'Doctypes' section was not found in the CI (column A of 'Project Requirements').");
        if (fieldsRow == 0)
            throw new InvalidDataException("The 'Project Fields' section was not found in the CI (column A of 'Project Requirements').");

        CiData ci = new()
        {
            FilePath = ciFile,
            Doctypes = ReadDoctypes(ws, doctypesRow, lastRow, lastCol),
            Fields = ReadFields(ws, fieldsRow, lastRow, lastCol)
        };

        if (ci.Doctypes.Count == 0)
            throw new InvalidDataException("No doctypes were found under the 'Doctypes' section of the CI.");
        if (ci.Fields.Count == 0)
            throw new InvalidDataException("No fields were found under the 'Project Fields' section of the CI.");

        return ci;
    }

    private static int FindSectionRow(IWorksheet ws, int lastRow, string section)
    {
        for (int row = 1; row <= lastRow; row++)
        {
            if (Normalize(ws.Range[row, 1].DisplayText).Equals(section, StringComparison.OrdinalIgnoreCase))
                return row;
        }

        return 0;
    }

    private static List<DoctypeInfo> ReadDoctypes(IWorksheet ws, int sectionRow, int lastRow, int lastCol)
    {
        int headerRow = sectionRow + 1;
        int dataCol = 0, nonDataCol = 0, headerKeyOnlyCol = 0;

        for (int col = 1; col <= lastCol; col++)
        {
            string header = Normalize(ws.Range[headerRow, col].DisplayText).Replace("-", "").Replace(" ", "");

            if (header.Equals("Data", StringComparison.OrdinalIgnoreCase)) dataCol = col;
            else if (header.Equals("NonData", StringComparison.OrdinalIgnoreCase)) nonDataCol = col;
            else if (header.Equals("HeaderKeyOnly", StringComparison.OrdinalIgnoreCase)) headerKeyOnlyCol = col;
        }

        List<DoctypeInfo> doctypes = new();

        for (int row = headerRow + 1; row <= lastRow; row++)
        {
            string name = Normalize(ws.Range[row, 1].DisplayText);

            // the doctype list ends at the first blank row, or when the next section starts
            if (name.Length == 0 || name.Equals("Project Fields", StringComparison.OrdinalIgnoreCase)) break;

            doctypes.Add(new DoctypeInfo(
                name,
                IsMarked(ws, row, dataCol),
                IsMarked(ws, row, nonDataCol),
                IsMarked(ws, row, headerKeyOnlyCol)));
        }

        return doctypes;
    }

    private static List<CiField> ReadFields(IWorksheet ws, int sectionRow, int lastRow, int lastCol)
    {
        int headerRow = sectionRow + 1;
        int labelCol = 0, nameCol = 0, headerFieldCol = 0, dictionaryCol = 0;

        for (int col = 1; col <= lastCol; col++)
        {
            string header = Normalize(ws.Range[headerRow, col].DisplayText);

            if (header.Equals("Field Label", StringComparison.OrdinalIgnoreCase)) labelCol = col;
            else if (header.Equals("Field Name", StringComparison.OrdinalIgnoreCase)) nameCol = col;
            else if (header.Equals("Header Field", StringComparison.OrdinalIgnoreCase)) headerFieldCol = col;
            else if (header.Equals("Keying Dictionary", StringComparison.OrdinalIgnoreCase)) dictionaryCol = col;
        }

        if (labelCol == 0 || nameCol == 0)
            throw new InvalidDataException("The 'Project Fields' section has no 'Field Label' / 'Field Name' columns.");

        List<CiField> fields = new();
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        for (int row = headerRow + 1; row <= lastRow; row++)
        {
            string label = Normalize(ws.Range[row, labelCol].DisplayText);

            // the footnote / next section ends the field list
            if (label.StartsWith('*') || label.Equals("Special Keying Instructions", StringComparison.OrdinalIgnoreCase)) break;

            string name = StripWhitespace(ws.Range[row, nameCol].DisplayText);
            if (name.Length == 0 || !seen.Add(name)) continue;

            fields.Add(new CiField
            {
                Label = label.Length > 0 ? label : name,
                Name = name,
                IsHeader = IsMarked(ws, row, headerFieldCol),
                Dictionaries = dictionaryCol > 0 ? DictionaryNames(ws.Range[row, dictionaryCol].DisplayText) : Array.Empty<string>()
            });
        }

        return fields;
    }

    /// <summary>
    /// The dictionary names in a "Keying Dictionary" cell. A cell can hold several, separated by ";" or "," and sometimes
    /// with stray characters, so only the Dict_xxx words are taken.
    /// </summary>
    private static List<string> DictionaryNames(string? cell)
    {
        if (string.IsNullOrWhiteSpace(cell)) return new List<string>();

        return Regex.Matches(cell, @"Dict_\w+")
            .Select(m => m.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsMarked(IWorksheet ws, int row, int col) =>
        col > 0 && ws.Range[row, col].DisplayText.Trim().Equals("x", StringComparison.OrdinalIgnoreCase);

    /// <summary>Trims and removes control / zero-width characters, turning NBSP into a normal space.</summary>
    private static string Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        return string.Concat(input.Trim().Where(c => !char.IsControl(c)))
            .Replace("​", "")
            .Replace(" ", " ")
            .Trim();
    }

    /// <summary>Field names are identifiers, so every kind of whitespace is noise.</summary>
    private static string StripWhitespace(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        return string.Concat(input.Where(c => !char.IsWhiteSpace(c) && !TextUtil.IsInvisible(c)));
    }
}
