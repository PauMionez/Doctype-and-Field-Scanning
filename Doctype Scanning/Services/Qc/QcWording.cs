using System.Text.RegularExpressions;

namespace Doctype_Scanning.Services.Qc;

/// <summary>Wording rules for a header place value (check 2) and the plain form of a value used when comparing values.</summary>
internal static class QcWording
{
    private static readonly string[] BannedWords = { "Rural Dist", "District", "Townland" };

    private static readonly Regex StartsWithOfIn = new(@"^\s*(of|in)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex LeadingOfIn = new(@"^(of|in)\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ContainsCo = new(@"\bCo\.", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex LeadingCo = new(@"^Co\.?\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>What is wrong with the wording of the value; empty when it is fine.</summary>
    public static List<string> Problems(string value)
    {
        List<string> problems = new();

        if (StartsWithOfIn.IsMatch(value)) problems.Add("starts with 'of'/'in'");

        foreach (string word in BannedWords)
        {
            if (value.Contains(word, StringComparison.OrdinalIgnoreCase)) problems.Add($"contains '{word}'");
        }

        if (ContainsCo.IsMatch(value)) problems.Add("contains 'Co.'");

        if (value.StartsWith(' ') || value.EndsWith(' ') || value.Contains("  ")) problems.Add("extra spaces");

        return problems;
    }

    /// <summary>
    /// The value without the wording noise: spaces cleaned and a leading "of" / "in" / "Co." removed
    /// ("of Lettertrim" -> "Lettertrim"). Values are compared in this form so wording does not hide a typo.
    /// </summary>
    public static string Plain(string value)
    {
        string plain = TextUtil.Clean(value);

        string previous;
        do
        {
            previous = plain;
            plain = LeadingOfIn.Replace(plain, string.Empty);
            plain = LeadingCo.Replace(plain, string.Empty);
        }
        while (plain != previous);

        return plain.Trim();
    }
}
