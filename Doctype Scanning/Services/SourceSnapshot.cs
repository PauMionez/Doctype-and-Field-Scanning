using Doctype_Scanning.Models;

namespace Doctype_Scanning.Services;

internal readonly record struct ImageRow(long Id, string Name, string? DocType);

/// <summary>Records of an image. FirstId is null when the source (an excel export) has no record ids.</summary>
internal readonly record struct RecordStats(long Count, long? FirstId);

/// <summary>
/// Everything the checker needs from one db3 or one excel export, read into the same shape
/// so both kinds of input are judged by the same rules.
/// </summary>
internal sealed class SourceSnapshot
{
    public List<ImageRow> Images { get; } = new();

    public Dictionary<long, RecordStats> RecordStats { get; } = new();

    /// <summary>True when the checked field was found as a header field, so a blank value can be reported.</summary>
    public bool FieldIsHeader { get; set; }

    /// <summary>Value of the checked header field per image, used to catch non-data images that carry it.</summary>
    public Dictionary<long, string> HeaderValues { get; } = new();

    /// <summary>Every keyed value of the checked field, for the typo pass.</summary>
    public List<ValueEntry> Values { get; } = new();

    /// <summary>Doctypes listed in the db3's DocType table (empty for excel).</summary>
    public List<string> DocTypeTableEntries { get; } = new();

    /// <summary>Set when the checked field could not be found in the source.</summary>
    public string? FieldProblem { get; set; }
}
