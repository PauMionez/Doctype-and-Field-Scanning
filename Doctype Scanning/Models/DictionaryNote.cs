namespace Doctype_Scanning.Models;

/// <summary>
/// A comment, not an error: the value the files use most is not in the dictionary. It is not flagged as a typo
/// (it is the common value), but the reviewer is told, because the dictionary may be what is missing it.
/// </summary>
public sealed record DictionaryNote(string Field, string Value, int Count, int Total, string Dictionary)
{
    public string Message => $"Most common value ({Count} of {Total}) but it is not in {Dictionary} - not flagged as a typo";
}
