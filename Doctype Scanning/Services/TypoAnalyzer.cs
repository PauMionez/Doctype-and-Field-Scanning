using Doctype_Scanning.Models;

namespace Doctype_Scanning.Services;

/// <summary>
/// Finds typos in the values of one field, judging each value against all the other values keyed
/// for that field in the selected db3 files (there is no dictionary to compare to, so the data is the reference).
///
///  - invisible / non-breaking characters, extra spaces
///  - characters that almost no other value of the field has (digits, symbols)
///  - letters in a field that is otherwise numeric
///  - casing that differs from the form used by the rest of the data
///  - a rare value one or two edits away from a much more common value ("Cadderr" vs "Cadder")
/// </summary>
internal static class TypoAnalyzer
{
    // a character found in less than this share of the values is considered unexpected for the field
    private const double RareCharShare = 0.05;

    // share of digit-only values above which the field is treated as numeric
    private const double NumericFieldShare = 0.9;

    // a value must be at least this common, and this many times more common than the suspect, to be offered as the correction
    private const int MinCommonCount = 3;
    private const int CommonToRareRatio = 10;

    // shorter values are too ambiguous to compare ("Ayr" vs "Air")
    private const int MinFuzzyLength = 5;

    // characters that are normal in names / places and never count as unexpected
    private const string AllowedPunctuation = "'’-.,()&/?[]*";

    // fields with at most this many distinct values, each used this often on average, are treated as a controlled list
    private const int ControlledMaxDistinct = 50;
    private const int ControlledMinAverageCount = 4;
    private const int ControlledRatio = 3;

    private sealed record Candidate(string Key, string Display, int Count);

    /// <summary>What the whole data set of the field looks like, computed once and used to judge every value.</summary>
    private sealed record Profile(
        bool NumericField,
        HashSet<char> RareChars,
        Dictionary<string, KeyValuePair<string, int>> CasingWinner,
        Dictionary<string, Candidate> CandidateByKey,
        Dictionary<int, List<Candidate>> CommonByLength,
        List<Candidate> Commons,
        bool Controlled,
        int Ratio);

    /// <param name="dictionary">
    /// The dictionary of the field, when there is one: it replaces "the most common value in the batch" as the
    /// reference, so a value is right when it is in the dictionary and wrong when it is not.
    /// </param>
    /// <param name="notes">Receives a comment for the most common value when it is not in the dictionary (it is not flagged as a typo).</param>
    public static List<ErrorRecord> Analyze(IReadOnlyList<ValueEntry> values, CancellationToken ct, DictionaryLookup? dictionary = null, string fieldLabel = "", List<DictionaryNote>? notes = null)
    {
        List<ErrorRecord> result = new();
        if (values.Count == 0) return result;

        if (dictionary != null) return AnalyzeWithDictionary(values, dictionary, fieldLabel, notes, ct);

        // cleaned form of every distinct raw value, with how many times it was keyed
        Dictionary<string, int> variantCounts = new(StringComparer.Ordinal);
        foreach (ValueEntry entry in values)
        {
            string cleaned = TextUtil.Clean(entry.Value);
            if (cleaned.Length == 0) continue;

            variantCounts[cleaned] = variantCounts.GetValueOrDefault(cleaned) + 1;
        }

        int total = variantCounts.Values.Sum();

        bool numericField = total > 0
            && (double)variantCounts.Where(v => IsNumericLike(v.Key)).Sum(v => v.Value) / total >= NumericFieldShare;

        HashSet<char> rareChars = FindRareChars(variantCounts, total);

        // casing groups: same text ignoring case
        Dictionary<string, List<KeyValuePair<string, int>>> caseGroups = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, int> variant in variantCounts)
        {
            string key = variant.Key.ToLowerInvariant();
            if (!caseGroups.TryGetValue(key, out var group))
                caseGroups[key] = group = new();

            group.Add(variant);
        }

        // most common form of each casing group, when there is a clear winner
        Dictionary<string, KeyValuePair<string, int>> casingWinner = new(StringComparer.Ordinal);
        List<Candidate> candidates = new();
        foreach (var (key, group) in caseGroups)
        {
            var top = group.OrderByDescending(v => v.Value).ThenBy(v => v.Key, StringComparer.Ordinal).ToList();
            int groupCount = group.Sum(v => v.Value);

            candidates.Add(new Candidate(key, top[0].Key, groupCount));

            if (top.Count > 1 && top[0].Value > top[1].Value)
                casingWinner[key] = top[0];
        }

        // values common enough to be offered as a correction, bucketed by length and most common first
        Dictionary<int, List<Candidate>> commonByLength = candidates
            .Where(c => c.Count >= MinCommonCount && c.Key.Length >= MinFuzzyLength)
            .GroupBy(c => c.Key.Length)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(c => c.Count).ToList());

        // a field with a few distinct values that repeat a lot (city, parish, county) is a controlled list:
        // there, even a modest minority variant of the main value is suspicious
        bool controlled = !numericField
            && candidates.Count <= ControlledMaxDistinct
            && total >= candidates.Count * ControlledMinAverageCount;

        Profile profile = new(
            numericField,
            rareChars,
            casingWinner,
            candidates.ToDictionary(c => c.Key, StringComparer.Ordinal),
            commonByLength,
            candidates.Where(c => c.Count >= MinCommonCount).OrderByDescending(c => c.Count).ToList(),
            controlled,
            controlled ? ControlledRatio : CommonToRareRatio);

        Dictionary<string, List<(string Message, string Value)>> findingCache = new(StringComparer.Ordinal);

        int processed = 0;
        foreach (ValueEntry entry in values)
        {
            if ((++processed & 0x3FF) == 0) ct.ThrowIfCancellationRequested();

            if (!findingCache.TryGetValue(entry.Value, out var findings))
            {
                findings = Evaluate(entry.Value, profile, ct);
                findingCache[entry.Value] = findings;
            }

            foreach ((string message, string value) in findings)
                result.Add(new ErrorRecord(entry.Db3, message + entry.Location, value, entry.ImageName, entry.RecordId));
        }

        return result;
    }

    /// <summary>
    /// Judges every value against the dictionary: exact match is fine, the same value written with other case / punctuation
    /// is called out, anything else is "not in the dictionary" with the nearest dictionary value as the suggestion.
    /// The batch only decides between equally near suggestions (the one used more often wins).
    /// </summary>
    private static List<ErrorRecord> AnalyzeWithDictionary(IReadOnlyList<ValueEntry> values, DictionaryLookup dictionary, string fieldLabel, List<DictionaryNote>? notes, CancellationToken ct)
    {
        Dictionary<string, int> used = new(StringComparer.Ordinal);
        foreach (ValueEntry entry in values)
        {
            string key = DictionaryLookup.Key(entry.Value);
            if (key.Length > 0) used[key] = used.GetValueOrDefault(key) + 1;
        }

        int Frequency(string dictionaryValue) => used.GetValueOrDefault(DictionaryLookup.Key(dictionaryValue));

        // The value the files use most (counted exactly as keyed, so a variant of it does not count) is the common value:
        // when it is not in the dictionary it is not flagged as a typo, it gets a comment so the reviewer can see the
        // dictionary may be what is missing it. Ties: every tied value.
        Dictionary<string, int> exact = new(StringComparer.Ordinal);
        foreach (ValueEntry entry in values)
        {
            string cleanedEntry = TextUtil.Clean(entry.Value);
            if (cleanedEntry.Length > 0) exact[cleanedEntry] = exact.GetValueOrDefault(cleanedEntry) + 1;
        }

        int topCount = exact.Count == 0 ? 0 : exact.Values.Max();
        int totalValues = exact.Values.Sum();

        HashSet<string> common = new(StringComparer.Ordinal);
        if (topCount >= 2)
        {
            foreach (string value in exact.Where(kv => kv.Value == topCount).Select(kv => kv.Key).OrderBy(v => v, StringComparer.Ordinal))
            {
                if (dictionary.Contains(value)) continue;

                common.Add(value);
                notes?.Add(new DictionaryNote(fieldLabel, value, topCount, totalValues, dictionary.Names));
            }
        }

        List<ErrorRecord> result = new();
        Dictionary<string, List<(string Message, string Value)>> findingCache = new(StringComparer.Ordinal);

        int processed = 0;
        foreach (ValueEntry entry in values)
        {
            if ((++processed & 0x3FF) == 0) ct.ThrowIfCancellationRequested();

            if (!findingCache.TryGetValue(entry.Value, out var findings))
            {
                findings = EvaluateWithDictionary(entry.Value, dictionary, Frequency, common);
                findingCache[entry.Value] = findings;
            }

            foreach ((string message, string value) in findings)
                result.Add(new ErrorRecord(entry.Db3, message + entry.Location, value, entry.ImageName, entry.RecordId));
        }

        return result;
    }

    private static List<(string Message, string Value)> EvaluateWithDictionary(string raw, DictionaryLookup dictionary, Func<string, int> frequency, HashSet<string> common)
    {
        List<(string Message, string Value)> findings = new();

        if (raw.Any(TextUtil.IsInvisible))
            findings.Add(("[Typo] Invisible or non-breaking character", TextUtil.Visible(raw)));

        string cleaned = TextUtil.Clean(raw);
        if (cleaned.Length == 0)
        {
            findings.Add(("[Typo] Value contains only spaces / invisible characters", TextUtil.Visible(raw)));
            return findings;
        }

        if (raw.StartsWith(' ')) findings.Add(("[Typo] Extra leading space", raw));
        if (raw.EndsWith(' ')) findings.Add(("[Typo] Extra trailing space", raw));
        if (raw.Contains("  ")) findings.Add(("[Typo] Double space inside the value", raw));

        if (dictionary.Contains(cleaned)) return findings;

        // the value the files use most: a comment (see DictionaryNote), not a typo
        if (common.Contains(cleaned)) return findings;

        string? sameValue = dictionary.FindLoose(cleaned);
        if (sameValue != null)
        {
            findings.Add(($"[Typo] Not written as in the dictionary - '{sameValue}' (case / spacing / punctuation)", cleaned));
            return findings;
        }

        // "of Cushkillary" / "Co. Galway": the value is in the dictionary once the wording is taken off
        string plain = Qc.QcWording.Plain(cleaned);
        if (plain != cleaned && plain.Length > 0 && (dictionary.Contains(plain) || dictionary.FindLoose(plain) != null))
        {
            findings.Add(($"[Typo] Not in the dictionary ({dictionary.Names}) - extra wording, did you mean '{dictionary.FindLoose(plain) ?? plain}'?", cleaned));
            return findings;
        }

        // a short value has too few letters to say which dictionary value was meant
        int maxDistance = cleaned.Length >= 7 ? 2 : cleaned.Length >= 4 ? 1 : 0;
        var nearest = dictionary.Nearest(cleaned, maxDistance, frequency);

        // a variant of the common value ("Rural Dist of Clifden" for "Rural Dist. of Clifden") is a typo of it, whether or not the dictionary has it
        string? nearCommon = null;
        int nearCommonDistance = int.MaxValue;
        foreach (string value in common)
        {
            int distance = TextUtil.BoundedDistance(cleaned.ToLowerInvariant(), value.ToLowerInvariant(), Math.Max(maxDistance, 1));
            if (distance == 0 || distance > Math.Max(maxDistance, 1) || distance >= nearCommonDistance) continue;

            nearCommon = value;
            nearCommonDistance = distance;
        }

        if (nearCommon != null && (nearest == null || nearCommonDistance <= nearest.Value.Distance))
        {
            findings.Add(($"[Typo] Not in the dictionary ({dictionary.Names}) - did you mean '{nearCommon}'? (the most common value in the files, which is not in the dictionary)", cleaned));
            return findings;
        }

        findings.Add((nearest == null
            ? $"[Typo] Not in the dictionary ({dictionary.Names})"
            : $"[Typo] Not in the dictionary ({dictionary.Names}) - did you mean '{nearest.Value.Value}'?", cleaned));

        return findings;
    }

    /// <summary>The problems of one value: what is wrong (message) and the value it is about.</summary>
    private static List<(string Message, string Value)> Evaluate(string raw, Profile profile, CancellationToken ct)
    {
        List<(string Message, string Value)> findings = new();

        if (raw.Any(TextUtil.IsInvisible))
            findings.Add(("[Typo] Invisible or non-breaking character", TextUtil.Visible(raw)));

        string cleaned = TextUtil.Clean(raw);
        if (cleaned.Length == 0)
        {
            findings.Add(("[Typo] Value contains only spaces / invisible characters", TextUtil.Visible(raw)));
            return findings;
        }

        // the cell cannot show a space, so the message says which one it is
        if (raw.StartsWith(' ')) findings.Add(("[Typo] Extra leading space", raw));
        if (raw.EndsWith(' ')) findings.Add(("[Typo] Extra trailing space", raw));
        if (raw.Contains("  ")) findings.Add(("[Typo] Double space inside the value", raw));

        string unexpected = new(cleaned.Where(profile.RareChars.Contains).Distinct().ToArray());
        if (unexpected.Length > 0)
            findings.Add(($"[Typo] Unexpected character(s) '{unexpected}'", cleaned));

        if (profile.NumericField)
        {
            if (cleaned.Any(char.IsLetter))
                findings.Add(("[Typo] Contains letters in a numeric field", cleaned));

            return findings;
        }

        string key = cleaned.ToLowerInvariant();

        if (profile.CasingWinner.TryGetValue(key, out KeyValuePair<string, int> winner) && !winner.Key.Equals(cleaned, StringComparison.Ordinal))
        {
            findings.Add(($"[Typo] Inconsistent casing - the most common form is '{winner.Key}' ({winner.Value}x)", cleaned));
            return findings;
        }

        if (profile.CandidateByKey.TryGetValue(key, out Candidate? self))
        {
            Candidate? near = FindNearCommon(self, profile, ct);
            if (near != null)
            {
                findings.Add(($"[Typo] Possible typo - did you mean '{near.Display}' ({near.Count}x)?", cleaned));
            }
            else if (profile.Controlled)
            {
                string? wordLevel = FindWordLevelVariant(self, profile);
                if (wordLevel != null) findings.Add((wordLevel, cleaned));
            }
        }

        return findings;
    }

    /// <summary>
    /// Controlled lists only: the value is the main value written another way - an abbreviation
    /// ("Rural Dist of Clifden" / "Rural District of Clifden" for "Rural Dist. of Clifden") or with words missing
    /// ("of Clifden", "Clifden").
    /// </summary>
    private static string? FindWordLevelVariant(Candidate suspect, Profile profile)
    {
        string[] suspectWords = Words(suspect.Key);
        if (suspectWords.Length == 0) return null;

        int minCommon = Math.Max(MinCommonCount, suspect.Count * profile.Ratio);

        // commons are sorted by count: the first match is the most common form
        foreach (Candidate common in profile.Commons)
        {
            if (common.Count < minCommon) break;
            if (common.Key == suspect.Key) continue;

            string[] commonWords = Words(common.Key);

            if (SameWords(suspectWords, commonWords))
                return $"[Typo] Inconsistent spelling - the most common form is '{common.Display}' ({common.Count}x)";

            if (IsPartOf(suspectWords, commonWords))
                return $"[Typo] Incomplete value - the most common form is '{common.Display}' ({common.Count}x)";
        }

        return null;
    }

    /// <summary>Lower-case words without the dots and commas ("Dist." -> "dist").</summary>
    private static string[] Words(string key) =>
        key.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Trim('.', ',', ';', ':'))
            .Where(w => w.Length > 0)
            .ToArray();

    /// <summary>Same word, or one is an abbreviation of the other ("dist" / "district").</summary>
    private static bool SameWord(string a, string b) =>
        a == b || (Math.Min(a.Length, b.Length) >= 3 && (a.StartsWith(b, StringComparison.Ordinal) || b.StartsWith(a, StringComparison.Ordinal)));

    private static bool SameWords(string[] a, string[] b) =>
        a.Length == b.Length && a.Zip(b).All(p => SameWord(p.First, p.Second));

    /// <summary>The words of <paramref name="part"/> appear, in order and side by side, inside the longer <paramref name="whole"/>.</summary>
    private static bool IsPartOf(string[] part, string[] whole)
    {
        if (whole.Length <= part.Length) return false;

        for (int start = 0; start + part.Length <= whole.Length; start++)
        {
            bool match = true;
            for (int i = 0; i < part.Length && match; i++)
                match = SameWord(part[i], whole[start + i]);

            if (match) return true;
        }

        return false;
    }

    private static Candidate? FindNearCommon(Candidate suspect, Profile profile, CancellationToken ct)
    {
        Dictionary<int, List<Candidate>> commonByLength = profile.CommonByLength;

        // a changed digit is a different number, not a typo ("36 years" vs "63 years")
        if (suspect.Key.Length < MinFuzzyLength || suspect.Key.Any(char.IsDigit)) return null;

        int maxDistance = suspect.Key.Length >= 9 ? 2 : 1;
        int minCommon = Math.Max(MinCommonCount, suspect.Count * profile.Ratio);

        Candidate? best = null;
        int bestDistance = int.MaxValue;

        for (int length = suspect.Key.Length - maxDistance; length <= suspect.Key.Length + maxDistance; length++)
        {
            if (!commonByLength.TryGetValue(length, out List<Candidate>? bucket)) continue;

            foreach (Candidate candidate in bucket)
            {
                // buckets are sorted by count, so nothing further down is common enough
                if (candidate.Count < minCommon) break;
                if (candidate.Key == suspect.Key) continue;

                int distance = TextUtil.BoundedDistance(suspect.Key, candidate.Key, maxDistance);
                if (distance > maxDistance) continue;

                if (distance < bestDistance || (distance == bestDistance && candidate.Count > best!.Count))
                {
                    best = candidate;
                    bestDistance = distance;
                }
            }

            ct.ThrowIfCancellationRequested();
        }

        return best;
    }

    private static HashSet<char> FindRareChars(Dictionary<string, int> variantCounts, int total)
    {
        Dictionary<char, int> valuesWithChar = new();

        foreach (KeyValuePair<string, int> variant in variantCounts)
        {
            foreach (char c in variant.Key.Where(IsUnusual).Distinct())
                valuesWithChar[c] = valuesWithChar.GetValueOrDefault(c) + variant.Value;
        }

        return valuesWithChar
            .Where(kv => (double)kv.Value / total < RareCharShare)
            .Select(kv => kv.Key)
            .ToHashSet();
    }

    private static bool IsUnusual(char c) =>
        !char.IsLetter(c) && c != ' ' && !AllowedPunctuation.Contains(c)
        && char.GetUnicodeCategory(c) is not (System.Globalization.UnicodeCategory.NonSpacingMark or System.Globalization.UnicodeCategory.SpacingCombiningMark);

    private static bool IsNumericLike(string value) => value.Any(char.IsDigit) && !value.Any(char.IsLetter);
}
