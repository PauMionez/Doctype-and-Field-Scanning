namespace Doctype_Scanning.Models;

/// <summary>One row of the CI "Doctypes" section.</summary>
public sealed record DoctypeInfo(string Name, bool IsData, bool IsNonData, bool IsHeaderKeyOnly);

/// <summary>One row of the CI "Project Fields" section.</summary>
public sealed class CiField
{
    public string Label { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public bool IsHeader { get; init; }

    /// <summary>Text shown in the dropdown, e.g. "City Name (Header)".</summary>
    public string Display => IsHeader ? $"{Label} (Header)" : Label;
}

/// <summary>What the checker needs from the CI excel.</summary>
public sealed class CiData
{
    public string FilePath { get; init; } = string.Empty;
    public List<DoctypeInfo> Doctypes { get; init; } = new();
    public List<CiField> Fields { get; init; } = new();
}
