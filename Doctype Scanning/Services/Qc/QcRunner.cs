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
        [3] = "Typo (1-2 letters from a more common value)",
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

    public static QcResult Run(string file, string batchName, CiData ci, double captureLimitPercent)
    {
        QcBatch batch = QcBatchReader.Read(file, batchName, ci);

        List<QcFlag> flags = QcHeaderChecks.Run(batch.Images);
        flags.AddRange(QcDoctypeChecks.Run(batch.Images, captureLimitPercent));

        List<string> notes = new(batch.Notes)
        {
            "Check 4 (dictionary) was not run: the Dict_STND_Ireland dictionary files have not been provided yet."
        };

        return new QcResult
        {
            BatchName = batchName,
            ImageCount = batch.Images.Count,
            CaptureLimitPercent = captureLimitPercent,
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
}
