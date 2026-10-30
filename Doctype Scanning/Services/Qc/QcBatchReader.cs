using Doctype_Scanning.Models;

namespace Doctype_Scanning.Services.Qc;

/// <summary>
/// Reads one batch (a db3 or an excel export) into <see cref="QcBatch"/>. The QC fields are found by their CI field label,
/// so the same checks run on either kind of input.
/// </summary>
internal static class QcBatchReader
{
    public static QcBatch Read(string file, string batchName, CiData ci)
    {
        QcFieldMap map = QcFieldMap.Build(ci);

        QcBatch batch = SourceChecker.IsExcel(file)
            ? QcExcelReader.Read(file, batchName, map)
            : QcDb3Reader.Read(file, batchName, map);

        foreach (string label in map.Missing)
            batch.Notes.Add($"Field '{label}' is not in the CI - its checks were skipped.");

        batch.SortImages();
        return batch;
    }
}

/// <summary>CI field label -> CI field, for the labels the QC uses.</summary>
internal sealed class QcFieldMap
{
    private readonly Dictionary<string, CiField> _byLabel = new(StringComparer.Ordinal);

    public List<string> Missing { get; } = new();

    public static QcFieldMap Build(CiData ci)
    {
        QcFieldMap map = new();

        foreach (string label in QcFields.Header.Concat(QcFields.Record))
        {
            CiField? field = ci.Fields.FirstOrDefault(f => TextUtil.Loose(f.Label) == TextUtil.Loose(label));
            if (field == null) map.Missing.Add(label);
            else map._byLabel[label] = field;
        }

        return map;
    }

    public CiField? Get(string label) => _byLabel.GetValueOrDefault(label);
}
