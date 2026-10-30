using Doctype_Scanning.Models;

namespace Doctype_Scanning.Services.Qc;

/// <summary>
/// Header place-name checks 1, 2, 3, 4, 5, 6, 7, 8 and 9. An image is judged by one header value (not one per record)
/// and neighbours are the Census / Coverpage images next to it in ImageName order (Capture and other pages are skipped).
/// </summary>
internal static class QcHeaderChecks
{
    // a typo needs a value this many images common to be offered as the correction
    private const int MinCommonImages = 3;

    // values shorter than this are too ambiguous to compare
    private const int MinTypoLength = 4;

    /// <param name="dictionaries">Dictionary of each place field that has one (field label -> lookup); empty when no dictionary is imported.</param>
    public static List<QcFlag> Run(IReadOnlyList<QcImage> images, IReadOnlyDictionary<string, DictionaryLookup> dictionaries)
    {
        List<QcFlag> flags = new();
        List<QcImage> data = images.Where(QcDoctypes.IsData).ToList();

        BlankHeaders(data, flags);
        Wording(images, flags);
        HashSet<(string Image, string Field)> typoFlagged = new();
        Typos(images, dictionaries, flags, typoFlagged);
        DictionaryCheck(images, dictionaries, typoFlagged, flags);
        OneOffs(data, flags);
        CoverpageVsCensus(data, flags);
        SameImage(images, flags);
        WardAndElectoralDivision(images, flags);
        HeaderLanguage(images, flags);

        return flags;
    }

    /// <summary>Check 1: a place is blank on some Census / Coverpage images but filled on others in the batch.</summary>
    private static void BlankHeaders(List<QcImage> data, List<QcFlag> flags)
    {
        foreach (string field in QcFields.RequiredPlaces)
        {
            int filled = data.Count(i => IsFilled(i.HeaderValue(field)));
            if (filled == 0 || filled == data.Count) continue;

            for (int i = 0; i < data.Count; i++)
            {
                if (IsFilled(data[i].HeaderValue(field))) continue;

                flags.Add(new QcFlag(data[i].Name, field, string.Empty, 1,
                    $"Blank '{field}' - filled on {filled} of {data.Count} Census/Coverpage images in this batch",
                    NeighboursAgree(data, i, field)));
            }
        }
    }

    /// <summary>Check 2: wording that does not belong in a place value ("of ...", "Rural Dist", "District", "Townland", "Co.", extra spaces).</summary>
    private static void Wording(IReadOnlyList<QcImage> images, List<QcFlag> flags)
    {
        foreach (QcImage image in images)
        {
            foreach (string field in QcFields.Wording)
            {
                string value = image.HeaderValue(field);
                if (!IsFilled(value)) continue;

                List<string> problems = QcWording.Problems(value);
                if (problems.Count == 0) continue;

                // only offered when removing "of" / "Co." / spaces is all it takes
                string plain = QcWording.Plain(value);
                string suggested = plain.Length > 0 && plain != value && QcWording.Problems(plain).Count == 0 ? plain : string.Empty;

                flags.Add(new QcFlag(image.Name, field, value, 2, $"Form wording: {string.Join(", ", problems)}", suggested));
            }
        }
    }

    /// <summary>
    /// Check 3: a place that is 1-2 letters different from a value that is more likely right. For a field with a dictionary that is
    /// a dictionary value (a value that is in the dictionary is right, however rare it is in the batch) or a clearly more common
    /// value in the batch, whichever is closer; for a field without a dictionary it is a more common value in the batch.
    /// </summary>
    private static void Typos(IReadOnlyList<QcImage> images, IReadOnlyDictionary<string, DictionaryLookup> dictionaries, List<QcFlag> flags, HashSet<(string Image, string Field)> typoFlagged)
    {
        foreach (string field in QcFields.Places)
        {
            dictionaries.TryGetValue(field, out DictionaryLookup? lookup);

            // images per plain value (case-insensitive), with the most used written form to show as the correction
            Dictionary<string, Dictionary<string, int>> forms = new(StringComparer.Ordinal);
            Dictionary<string, int> used = new(StringComparer.Ordinal);
            foreach (QcImage image in images)
            {
                string plain = QcWording.Plain(image.HeaderValue(field));
                if (plain.Length == 0) continue;

                string key = plain.ToLowerInvariant();
                if (!forms.TryGetValue(key, out var written)) forms[key] = written = new(StringComparer.Ordinal);
                written[plain] = written.GetValueOrDefault(plain) + 1;

                string dictionaryKey = DictionaryLookup.Key(plain);
                used[dictionaryKey] = used.GetValueOrDefault(dictionaryKey) + 1;
            }

            List<(string Key, string Display, int Count)> values = forms
                .Select(f => (f.Key, f.Value.OrderByDescending(w => w.Value).First().Key, f.Value.Values.Sum()))
                .OrderByDescending(v => v.Item3)
                .ToList();

            foreach (QcImage image in images)
            {
                string value = image.HeaderValue(field);
                string plain = QcWording.Plain(value);
                if (plain.Length < MinTypoLength) continue;

                // in the dictionary: right (only its case / punctuation can be off, which is check 4's); without one a changed digit is not a typo
                if (lookup != null ? lookup.Contains(plain) || lookup.FindLoose(plain) != null : plain.Any(char.IsDigit)) continue;

                string key = plain.ToLowerInvariant();
                int count = forms[key].Values.Sum();
                int maxDistance = key.Length >= 7 ? 2 : 1;

                // a clearly more common value in the batch
                (string Key, string Display, int Count)? batch = null;
                int batchDistance = int.MaxValue;

                foreach (var candidate in values)
                {
                    if (candidate.Count < MinCommonImages || candidate.Count <= count) break;

                    int distance = TextUtil.BoundedDistance(key, candidate.Key, maxDistance);
                    if (distance == 0 || distance > maxDistance || distance >= batchDistance) continue;

                    batch = candidate;
                    batchDistance = distance;
                }

                // a dictionary value (equally near ones: the one the batch uses more)
                var dictionary = lookup?.Nearest(plain, maxDistance, d => used.GetValueOrDefault(DictionaryLookup.Key(d)));

                if (dictionary != null && (batch == null || dictionary.Value.Distance <= batchDistance))
                {
                    flags.Add(new QcFlag(image.Name, field, value, 3,
                        $"Typo: '{plain}' is {dictionary.Value.Distance} letter(s) different from the dictionary value '{dictionary.Value.Value}'",
                        dictionary.Value.Value));
                    typoFlagged.Add((image.Name, field));
                }
                else if (batch != null)
                {
                    flags.Add(new QcFlag(image.Name, field, value, 3,
                        $"Typo: '{plain}' is {batchDistance} letter(s) different from the more common '{batch.Value.Display}' ({batch.Value.Count} images)",
                        batch.Value.Display));
                    typoFlagged.Add((image.Name, field));
                }
            }
        }
    }

    /// <summary>
    /// Check 4: a place that is not in its dictionary (City, Townland, Electoral Division, County: Dict_STND_Ireland ...).
    /// A value check 3 already reported as a near-miss of a dictionary value is not listed twice.
    /// </summary>
    private static void DictionaryCheck(IReadOnlyList<QcImage> images, IReadOnlyDictionary<string, DictionaryLookup> dictionaries,
        HashSet<(string Image, string Field)> typoFlagged, List<QcFlag> flags)
    {
        foreach (string field in QcFields.Places)
        {
            if (!dictionaries.TryGetValue(field, out DictionaryLookup? lookup)) continue;

            foreach (QcImage image in images)
            {
                string value = image.HeaderValue(field);
                string plain = QcWording.Plain(value);

                // wording ("of ...", "Co.") is check 2's job: the value is looked up without it
                if (plain.Length == 0 || lookup.Contains(plain) || typoFlagged.Contains((image.Name, field))) continue;

                string? sameValue = lookup.FindLoose(plain);

                flags.Add(sameValue != null
                    ? new QcFlag(image.Name, field, value, QcRunner.DictionaryCheck,
                        $"Dictionary: not written as in {lookup.Names} - '{sameValue}' (case / spacing / punctuation)", sameValue)
                    : new QcFlag(image.Name, field, value, QcRunner.DictionaryCheck,
                        $"Dictionary: '{plain}' is not in {lookup.Names}", string.Empty));
            }
        }
    }

    /// <summary>Check 5: the value differs from both the previous and the next image, which match each other.</summary>
    private static void OneOffs(List<QcImage> data, List<QcFlag> flags)
    {
        for (int i = 1; i < data.Count - 1; i++)
        {
            foreach (string field in QcFields.Places)
            {
                string value = data[i].HeaderValue(field);
                string previous = data[i - 1].HeaderValue(field);
                string next = data[i + 1].HeaderValue(field);

                if (!IsFilled(value)) continue;

                // compared without the "of" / "Co." wording: a wording difference is check 2's job, and without this
                // a correct value between two "of ..." neighbours would be flagged as a one-off
                string plainPrevious = QcWording.Plain(previous);
                if (!Same(plainPrevious, QcWording.Plain(next)) || Same(QcWording.Plain(value), plainPrevious)) continue;

                flags.Add(new QcFlag(data[i].Name, field, value, 5,
                    $"One-off: differs from the previous ({data[i - 1].Name}) and next ({data[i + 1].Name}) image, which both have '{Shown(plainPrevious)}'",
                    plainPrevious));
            }
        }
    }

    /// <summary>Check 6: a Coverpage header that differs from the Census image that follows it.</summary>
    private static void CoverpageVsCensus(List<QcImage> data, List<QcFlag> flags)
    {
        for (int i = 0; i < data.Count - 1; i++)
        {
            if (!QcDoctypes.Is(data[i], QcDoctypes.Coverpage) || !QcDoctypes.Is(data[i + 1], QcDoctypes.Census)) continue;

            QcImage cover = data[i];
            QcImage census = data[i + 1];

            foreach (string field in QcFields.Places)
            {
                string coverValue = cover.HeaderValue(field);
                string censusValue = census.HeaderValue(field);
                if (Same(coverValue, censusValue)) continue;

                string suggested = QcWording.Plain(censusValue);
                flags.Add(new QcFlag(cover.Name, field, coverValue, 6,
                    $"Coverpage '{Shown(coverValue)}' differs from the next Census image {census.Name} '{Shown(censusValue)}'",
                    suggested));
            }
        }
    }

    /// <summary>Check 7: the records of one image (same section) carry different header values.</summary>
    private static void SameImage(IReadOnlyList<QcImage> images, List<QcFlag> flags)
    {
        foreach (QcImage image in images)
        {
            foreach (string field in QcFields.Wording)
            {
                if (!image.HeaderRows.TryGetValue(field, out var rows)) continue;

                foreach (var section in rows.GroupBy(r => r.Section))
                {
                    List<string> distinct = section
                        .Select(r => TextUtil.Clean(r.Value))
                        .Distinct(StringComparer.Ordinal)
                        .ToList();

                    if (distinct.Count < 2) continue;

                    flags.Add(new QcFlag(image.Name, field, string.Join(" | ", distinct.Select(Shown)), 7,
                        section.Key.Length > 0
                            ? $"Records of this image have different '{field}' values (section {section.Key})"
                            : $"Records of this image have different '{field}' values",
                        string.Empty));
                }
            }
        }
    }

    /// <summary>Check 8: Ward and Electoral Division are both filled on the same image.</summary>
    private static void WardAndElectoralDivision(IReadOnlyList<QcImage> images, List<QcFlag> flags)
    {
        foreach (QcImage image in images)
        {
            string ward = image.HeaderValue(QcFields.Ward);
            string division = image.HeaderValue(QcFields.ElectoralDivision);
            if (!IsFilled(ward) || !IsFilled(division)) continue;

            flags.Add(new QcFlag(image.Name, $"{QcFields.Ward} / {QcFields.ElectoralDivision}", $"Ward: {ward} | ED: {division}", 8,
                "Ward and Electoral Division are both filled on this image", string.Empty));
        }
    }

    /// <summary>Check 9: Header Language on a Census image must be "en" or "ga".</summary>
    private static void HeaderLanguage(IReadOnlyList<QcImage> images, List<QcFlag> flags)
    {
        foreach (QcImage image in images.Where(i => QcDoctypes.Is(i, QcDoctypes.Census)))
        {
            if (!image.HeaderRows.ContainsKey(QcFields.HeaderLanguage)) continue;

            string value = image.HeaderValue(QcFields.HeaderLanguage);
            string clean = TextUtil.Clean(value);
            if (clean is "en" or "ga") continue;

            string suggested = clean.ToLowerInvariant() is "en" or "ga" ? clean.ToLowerInvariant() : string.Empty;
            flags.Add(new QcFlag(image.Name, QcFields.HeaderLanguage, value, 9,
                clean.Length == 0 ? "Header Language is blank on a Census image (must be 'en' or 'ga')" : "Header Language on a Census image must be 'en' or 'ga'",
                suggested));
        }
    }

    private static bool IsFilled(string value) => TextUtil.Clean(value).Length > 0;

    private static bool Same(string a, string b) => string.Equals(TextUtil.Clean(a), TextUtil.Clean(b), StringComparison.Ordinal);

    private static string Shown(string value) => IsFilled(value) ? TextUtil.Clean(value) : "(blank)";

    /// <summary>The value both neighbours of image i agree on, when there is one.</summary>
    private static string NeighboursAgree(List<QcImage> data, int i, string field)
    {
        if (i == 0 || i == data.Count - 1) return string.Empty;

        string previous = QcWording.Plain(data[i - 1].HeaderValue(field));
        string next = QcWording.Plain(data[i + 1].HeaderValue(field));
        return IsFilled(previous) && Same(previous, next) ? previous : string.Empty;
    }
}
