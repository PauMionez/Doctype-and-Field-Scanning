using Doctype_Scanning.Models;
using System.IO;

namespace Doctype_Scanning.Services;

/// <summary>
/// Checks the DocType of every image and the typos of one field (picked from the CI) across db3 files and
/// excel exports of db3s.
///
/// DocType rules (judged against the doctypes of the CI):
///  - DocType blank, not in the CI (case / spacing differences and near-misses are called out)
///  - Data doctype without a record
///  - Non-Data doctype with records, or with a value in the checked header field
///  - Header Key Only doctype with records
///  - Data / Header Key Only doctype with a blank value in the checked header field
///  - DocType table entry of a db3 that is not in the CI
/// Typo rules live in <see cref="TypoAnalyzer"/>.
/// </summary>
public sealed class SourceChecker
{
    private static readonly string[] ExcelExtensions = { ".xlsx", ".xlsm" };

    private readonly CiData _ci;
    private readonly CiField _field;
    private readonly DictionaryLookup? _dictionary;
    private readonly List<DictionaryNote> _notes = new();

    private readonly Dictionary<string, DoctypeInfo> _exact = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DoctypeInfo> _loose = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (DoctypeInfo? Match, string? Problem)> _resolved = new(StringComparer.Ordinal);

    public SourceChecker(CiData ci, CiField field, DictionaryLibrary? dictionaries = null)
    {
        _ci = ci;
        _field = field;
        _dictionary = dictionaries?.Lookup(field.Dictionaries);

        foreach (DoctypeInfo doctype in ci.Doctypes)
        {
            _exact.TryAdd(doctype.Name, doctype);
            _loose.TryAdd(TextUtil.Loose(doctype.Name), doctype);
        }
    }

    /// <summary>Comments of the last <see cref="Run"/>: the most common value is not in the dictionary (not flagged as a typo).</summary>
    public IReadOnlyList<DictionaryNote> Notes => _notes;

    public static bool IsExcel(string file) =>
        ExcelExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase);

    /// <param name="files">db3 and / or excel export files</param>
    /// <param name="rootFolder">files under it are reported with their path relative to it, others by file name</param>
    public List<ErrorRecord> Run(IReadOnlyList<string> files, string rootFolder, IProgress<(int Percent, string Text)>? progress, CancellationToken ct)
    {
        _notes.Clear();
        List<ErrorRecord> errors = new();
        List<ValueEntry> values = new();

        for (int i = 0; i < files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            string name = DisplayName(files[i], rootFolder);
            progress?.Report((i * 80 / files.Count, $"Scanning {name} ({i + 1}/{files.Count})"));

            try
            {
                SourceSnapshot snapshot = IsExcel(files[i])
                    ? ExcelDataReader.Read(files[i], name, _field)
                    : Db3Reader.Read(files[i], name, _field);

                CheckDoctypes(snapshot, name, errors);
                values.AddRange(snapshot.Values);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors.Add(new ErrorRecord(name, $"Could not read the file: {ex.Message}", string.Empty, string.Empty, null));
            }
        }

        progress?.Report((85, _dictionary == null
            ? $"Checking {values.Count:N0} '{_field.Label}' value(s) for typos (most common value as reference)..."
            : $"Checking {values.Count:N0} '{_field.Label}' value(s) against {_dictionary.Names}..."));
        errors.AddRange(TypoAnalyzer.Analyze(values, ct, _dictionary, _field.Label, _notes));

        return errors
            .OrderBy(e => e.Db3, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.ImageName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.RecordId ?? long.MaxValue)
            .ThenBy(e => e.Message, StringComparer.Ordinal)
            .ToList();
    }

    private static string DisplayName(string file, string rootFolder)
    {
        string relative = Path.GetRelativePath(rootFolder, file);
        return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? Path.GetFileName(file) : relative;
    }

    private void CheckDoctypes(SourceSnapshot snapshot, string sourceName, List<ErrorRecord> errors)
    {
        if (snapshot.FieldProblem != null)
            errors.Add(new ErrorRecord(sourceName, snapshot.FieldProblem, string.Empty, string.Empty, null));

        foreach (string type in snapshot.DocTypeTableEntries)
        {
            string? problem = Resolve(type).Problem;
            if (problem != null)
                errors.Add(new ErrorRecord(sourceName, $"[DocType] {problem} (DocType table)", TextUtil.Visible(type), string.Empty, null));
        }

        foreach (ImageRow image in snapshot.Images)
        {
            (DoctypeInfo? info, string? problem) = Resolve(image.DocType);
            snapshot.RecordStats.TryGetValue(image.Id, out RecordStats stats);
            long? firstRecordId = stats.Count > 0 ? stats.FirstId : null;

            if (problem != null)
                errors.Add(new ErrorRecord(sourceName, $"[DocType] {problem}", TextUtil.Visible(image.DocType ?? string.Empty), image.Name, firstRecordId));

            if (info == null) continue;

            if (info.IsHeaderKeyOnly)
            {
                if (stats.Count > 0)
                    errors.Add(new ErrorRecord(sourceName, $"[DocType] Header Key Only DocType has {stats.Count} record(s)", info.Name, image.Name, firstRecordId));
            }
            else if (info.IsNonData)
            {
                if (stats.Count > 0)
                    errors.Add(new ErrorRecord(sourceName, $"[DocType] Non-Data DocType has {stats.Count} record(s)", info.Name, image.Name, firstRecordId));

                if (snapshot.HeaderValues.TryGetValue(image.Id, out string? headerValue))
                    errors.Add(new ErrorRecord(sourceName, $"[DocType] Non-Data DocType '{info.Name}' has a value in '{_field.Label}'", headerValue, image.Name, firstRecordId));
            }
            else if (info.IsData && stats.Count == 0)
            {
                errors.Add(new ErrorRecord(sourceName, "[DocType] Data DocType has no record", info.Name, image.Name, null));
            }

            // a keyed image (Data or Header Key Only) is expected to carry the header value
            bool keyed = info.IsHeaderKeyOnly || (info.IsData && !info.IsNonData);
            if (snapshot.FieldIsHeader && keyed && !snapshot.HeaderValues.ContainsKey(image.Id))
                errors.Add(new ErrorRecord(sourceName, $"[DocType] {(info.IsHeaderKeyOnly ? "Header Key Only" : "Data")} DocType '{info.Name}' has a blank '{_field.Label}' header", string.Empty, image.Name, firstRecordId));
        }
    }

    /// <summary>
    /// Matches a doctype to the CI. Problem is null only for an exact match; a case / spacing difference still
    /// returns the CI doctype so the image can be judged by its rules.
    /// </summary>
    private (DoctypeInfo? Match, string? Problem) Resolve(string? docType)
    {
        string key = docType ?? string.Empty;
        if (_resolved.TryGetValue(key, out var cached)) return cached;

        (DoctypeInfo? Match, string? Problem) result;

        if (string.IsNullOrWhiteSpace(docType))
        {
            result = (null, "DocType is blank");
        }
        else if (_exact.TryGetValue(docType, out DoctypeInfo? exact))
        {
            result = (exact, null);
        }
        else
        {
            string loose = TextUtil.Loose(docType);

            if (_loose.TryGetValue(loose, out DoctypeInfo? same))
            {
                result = (same, $"DocType does not exactly match CI doctype '{same.Name}' (case / spacing)");
            }
            else
            {
                int max = loose.Length <= 4 ? 1 : 2;
                DoctypeInfo? best = null;
                int bestDistance = int.MaxValue;

                foreach (DoctypeInfo candidate in _ci.Doctypes)
                {
                    int distance = TextUtil.BoundedDistance(loose, TextUtil.Loose(candidate.Name), max);
                    if (distance <= max && distance < bestDistance)
                    {
                        best = candidate;
                        bestDistance = distance;
                    }
                }

                result = (null, best != null
                    ? $"DocType not found in the CI - possible typo of '{best.Name}'"
                    : "DocType not found in the CI");
            }
        }

        _resolved[key] = result;
        return result;
    }
}
