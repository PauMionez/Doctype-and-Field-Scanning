namespace Doctype_Scanning.Services.Qc;

/// <summary>The fields the 63564.IDX.001 QC looks at, named by their CI field label.</summary>
internal static class QcFields
{
    public const string City = "City";
    public const string Townland = "Townland";
    public const string Ward = "Ward";
    public const string ElectoralDivision = "Electoral Division";
    public const string County = "County";
    public const string StreetAddress = "Street Address";
    public const string HeaderLanguage = "Header Language";

    public const string LineNumber = "Line Number";
    public const string Age = "Age";
    public const string Gender = "Gender";
    public const string NumberOfMales = "Number of Males";
    public const string NumberOfFemales = "Number of Females";
    public const string TotalPersons = "Total Number of Persons";
    public const string RoomsOccupied = "Number of Rooms Occupied";

    public static readonly string[] Header =
        { City, Townland, Ward, ElectoralDivision, County, StreetAddress, HeaderLanguage };

    public static readonly string[] Record =
        { LineNumber, Age, Gender, NumberOfMales, NumberOfFemales, TotalPersons, RoomsOccupied };

    /// <summary>Place names: checked for blanks, typos, one-offs and Coverpage / Census agreement.</summary>
    public static readonly string[] Places = { City, Townland, Ward, ElectoralDivision, County };

    /// <summary>Places that every Census / Coverpage image is expected to carry (Ward and Street Address are optional).</summary>
    public static readonly string[] RequiredPlaces = { City, Townland, ElectoralDivision, County };

    /// <summary>Every header field that is a place or an address: checked for wording and for variation inside an image.</summary>
    public static readonly string[] Wording = { City, Townland, Ward, ElectoralDivision, County, StreetAddress };
}

/// <summary>DocType names of the project and how an image is classified.</summary>
internal static class QcDoctypes
{
    public const string Census = "Census";
    public const string Coverpage = "Coverpage";
    public const string Capture = "Capture";

    public static bool Is(QcImage image, string doctype) => TextUtil.Loose(image.DocType) == doctype.ToLowerInvariant();

    public static bool IsData(QcImage image) => Is(image, Census) || Is(image, Coverpage);
}

/// <summary>
/// One image of a batch with what the QC needs: the doctype, the header values as keyed on each record row
/// (an image is judged by one header value, not one per record) and which record fields have data.
/// </summary>
internal sealed class QcImage
{
    public string Name { get; init; } = string.Empty;

    public string DocType { get; set; } = string.Empty;

    public int RecordCount { get; set; }

    /// <summary>True when anything at all is keyed on the image (any field, header or record).</summary>
    public bool HasKeyedData { get; set; }

    /// <summary>Header field label -> the value on each record row / header row, with its section. Blank values are kept.</summary>
    public Dictionary<string, List<(string Section, string Value)>> HeaderRows { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Record field label -> first value keyed on the image.</summary>
    public Dictionary<string, string> RecordValues { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void AddHeader(string field, string section, string value)
    {
        if (!HeaderRows.TryGetValue(field, out var rows))
            HeaderRows[field] = rows = new();

        rows.Add((section, value));
    }

    /// <summary>The header value of the image: the most common non-blank value on its rows, blank when there is none.</summary>
    public string HeaderValue(string field)
    {
        if (!HeaderRows.TryGetValue(field, out var rows)) return string.Empty;

        return rows
            .Select(r => r.Value)
            .Where(v => TextUtil.Clean(v).Length > 0)
            .GroupBy(v => v, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault() ?? string.Empty;
    }

    /// <summary>The keyed value of any QC field, header or record.</summary>
    public string Value(string field) =>
        RecordValues.TryGetValue(field, out string? record) ? record : HeaderValue(field);
}

/// <summary>A batch read for the QC: its images in ImageName order.</summary>
internal sealed class QcBatch
{
    public string Name { get; init; } = string.Empty;

    public List<QcImage> Images { get; } = new();

    public List<string> Notes { get; } = new();

    public void SortImages() =>
        Images.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
}
