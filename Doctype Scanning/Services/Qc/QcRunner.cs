using Doctype_Scanning.Models;

namespace Doctype_Scanning.Services.Qc;

/// <summary>Runs the pre-delivery QC on one batch. Read-only: it flags, it never changes the keyed data.</summary>
public static class QcRunner
{
    public const int DictionaryCheck = 4;

    public static readonly IReadOnlyDictionary<int, string> CheckNames = new Dictionary<int, string>
    {
        [1] = "Blank header",
        [2] = "Form wording",
        [3] = "Typo (1-2 letters from a dictionary / more common value)",
        [4] = "Dictionary (Dict_STND_Ireland)",
        [5] = "One-off value",
        [6] = "Coverpage vs next Census",
        [7] = "Different header values on one image",
        [8] = "Ward and Electoral Division both filled",
        [9] = "Header Language on Census",
        [10] = "Wrong fields for the DocType",
        [11] = "Capture with keyed data / inside a data run",
        [12] = "Capture share above the limit"
    };

    /// <param name="dictionaries">
    /// The imported dictionary file, or null. The dictionary of each place field comes from the CI "Keying Dictionary" column;
    /// a field with one is checked against it (checks 3 and 4), the others against the most common value in the batch (check 3).
    /// </param>
    public static QcResult Run(string file, string batchName, CiData ci, double captureLimitPercent, DictionaryLibrary? dictionaries = null)
    {
        QcBatch batch = QcBatchReader.Read(file, batchName, ci);

        List<string> notes = new(batch.Notes);
        Dictionary<string, DictionaryLookup> lookups = FindDictionaries(ci, dictionaries, notes);

        List<QcFlag> flags = QcHeaderChecks.Run(batch.Images, lookups);
        flags.AddRange(QcDoctypeChecks.Run(batch.Images, captureLimitPercent));

        return new QcResult
        {
            BatchName = batchName,
            ImageCount = batch.Images.Count,
            CaptureLimitPercent = captureLimitPercent,
            DictionaryRun = lookups.Count > 0,
            DocTypeCounts = batch.Images
                .GroupBy(i => i.DocType.Length == 0 ? "(blank)" : i.DocType)
                .Select(g => new KeyValuePair<string, int>(g.Key, g.Count()))
                .ToList(),
            Notes = notes,
            Flags = flags
                .OrderBy(f => f.ImageName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(f => f.Check)
                .ThenBy(f => f.Field, StringComparer.Ordinal)
                .ToList()
        };
    }

    /// <summary>Place field label -> its dictionary, for the fields whose CI dictionary is in the imported file. Says in the notes what was and was not used.</summary>
    private static Dictionary<string, DictionaryLookup> FindDictionaries(CiData ci, DictionaryLibrary? library, List<string> notes)
    {
        Dictionary<string, DictionaryLookup> lookups = new();

        if (library == null)
        {
            notes.Add("Check 4 (dictionary) was not run: no dictionary was imported. Check 3 compares with the most common value in the batch.");
            return lookups;
        }

        foreach (string label in QcFields.Places)
        {
            CiField? field = ci.Fields.FirstOrDefault(f => TextUtil.Loose(f.Label) == TextUtil.Loose(label));
            if (field == null || field.Dictionaries.Count == 0) continue;

            DictionaryLookup? lookup = library.Lookup(field.Dictionaries);

            if (lookup != null) lookups[label] = lookup;
            else notes.Add($"'{label}': dictionary {string.Join(", ", field.Dictionaries)} is not in the imported dictionary file - checked against the most common value in the batch instead.");
        }

        if (lookups.Count == 0)
            notes.Add("Check 4 (dictionary) was not run: none of the place fields has its dictionary in the imported file.");
        else
            notes.Add("Dictionaries used: " + string.Join("; ", lookups.Select(l => $"{l.Key} = {l.Value.Names}")));

        return lookups;
    }
}
