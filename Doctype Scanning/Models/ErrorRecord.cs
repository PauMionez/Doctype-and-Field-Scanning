namespace Doctype_Scanning.Models;

/// <summary>
/// One row of the output excel. Value is the offending value (a doctype, the field value) and is empty when the
/// error is not about one value. RecordId is null when the image has no record to point at.
/// </summary>
public sealed record ErrorRecord(string Db3, string Message, string Value, string ImageName, long? RecordId);

/// <summary>
/// A single keyed value of the checked field, collected from a db3 / excel for the typo pass.
/// Location is appended to the message when there is no record id to point at (excel rows: " (sequence 3)").
/// </summary>
internal sealed record ValueEntry(string Db3, string ImageName, long? RecordId, string Value, string Location = "");
