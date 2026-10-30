namespace Doctype_Scanning.Models;

/// <summary>One flagged image of the pre-delivery QC. A reviewer marks each flag "Error" or "Not an error" in the report.</summary>
public sealed record QcFlag(string ImageName, string Field, string KeyedValue, int Check, string Message, string Suggested);

/// <summary>The QC result of one batch (one db3 or one excel).</summary>
public sealed class QcResult
{
    public string BatchName { get; init; } = string.Empty;

    public int ImageCount { get; init; }

    /// <summary>Number of images per DocType, in order of first appearance.</summary>
    public List<KeyValuePair<string, int>> DocTypeCounts { get; init; } = new();

    public List<QcFlag> Flags { get; init; } = new();

    /// <summary>Things the reviewer should know: a field that was not found, a check that was not run.</summary>
    public List<string> Notes { get; init; } = new();

    public double CaptureLimitPercent { get; init; }

    /// <summary>True when a dictionary was available for at least one field, so check 4 ran.</summary>
    public bool DictionaryRun { get; init; }
}
