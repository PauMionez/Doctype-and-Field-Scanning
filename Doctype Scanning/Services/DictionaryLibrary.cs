using System.Text;

namespace Doctype_Scanning.Services;

/// <summary>
/// The keying dictionaries of an imported dictionary file (e.g. Dictionary_24015.txt), by name.
/// A field's dictionaries are named in the "Keying Dictionary" column of the CI.
/// </summary>
public sealed class DictionaryLibrary
{
    private readonly Dictionary<string, WordList> _lists = new(StringComparer.OrdinalIgnoreCase);

    public string FilePath { get; init; } = string.Empty;

    public int Count => _lists.Count;

    public int ValueCount => _lists.Values.Sum(l => l.Count);

    public bool Has(string name) => _lists.ContainsKey(name);

    public int CountOf(string name) => _lists.TryGetValue(name, out WordList? list) ? list.Count : 0;

    internal void Add(string name, WordList list) => _lists[name] = list;

    /// <summary>The dictionaries of a field, merged into one lookup. Null when none of the names is in the file.</summary>
    public DictionaryLookup? Lookup(IEnumerable<string> names)
    {
        List<WordList> found = names
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(_lists.ContainsKey)
            .Select(n => _lists[n])
            .ToList();

        return found.Count == 0 ? null : new DictionaryLookup(found);
    }
}

/// <summary>The values of one dictionary: exact lookup, lookup ignoring case / punctuation, and the nearest value.</summary>
internal sealed class WordList
{
    private readonly object _lock = new();
    private Dictionary<int, List<(string Key, string Display)>>? _byLength;

    public WordList(string name) => Name = name;

    public string Name { get; }

    public int Count => Exact.Count;

    public HashSet<string> Exact { get; } = new(StringComparer.Ordinal);

    /// <summary>Comparison key (lower case, punctuation and spacing ignored) -> the value as written in the dictionary.</summary>
    public Dictionary<string, string> ByKey { get; } = new(StringComparer.Ordinal);

    public void Add(string value)
    {
        string clean = TextUtil.Clean(value);
        if (clean.Length == 0 || !Exact.Add(clean)) return;

        ByKey.TryAdd(DictionaryLookup.Key(clean), clean);
    }

    /// <summary>Values bucketed by key length, so a nearest-match search only looks at similar lengths.</summary>
    public IReadOnlyDictionary<int, List<(string Key, string Display)>> ByLength()
    {
        lock (_lock)
        {
            return _byLength ??= ByKey
                .GroupBy(kv => kv.Key.Length)
                .ToDictionary(g => g.Key, g => g.Select(kv => (kv.Key, kv.Value)).ToList());
        }
    }
}

/// <summary>One or more dictionaries of a field, searched together.</summary>
public sealed class DictionaryLookup
{
    private readonly List<WordList> _lists;

    internal DictionaryLookup(List<WordList> lists) => _lists = lists;

    /// <summary>Names of the dictionaries, e.g. "Dict_STND_Ireland_City_Key".</summary>
    public string Names => string.Join(" / ", _lists.Select(l => l.Name));

    public int ValueCount => _lists.Sum(l => l.Count);

    /// <summary>The value is in a dictionary exactly as written.</summary>
    public bool Contains(string value)
    {
        string clean = TextUtil.Clean(value);
        return _lists.Any(l => l.Exact.Contains(clean));
    }

    /// <summary>The dictionary value that is the same apart from case, spacing and punctuation ("Rural Dist of X" / "Rural Dist. of X").</summary>
    public string? FindLoose(string value)
    {
        string key = Key(value);
        if (key.Length == 0) return null;

        foreach (WordList list in _lists)
        {
            if (list.ByKey.TryGetValue(key, out string? display)) return display;
        }

        return null;
    }

    /// <summary>
    /// The dictionary value closest to <paramref name="value"/> within <paramref name="maxDistance"/> letters (1 or more).
    /// Among equally close values the one used most often in the batch (<paramref name="frequency"/>) wins.
    /// </summary>
    public (string Value, int Distance)? Nearest(string value, int maxDistance, Func<string, int>? frequency = null)
    {
        string key = Key(value);
        if (key.Length == 0 || maxDistance < 1) return null;

        string? best = null;
        int bestDistance = int.MaxValue;
        int bestFrequency = -1;

        foreach (WordList list in _lists)
        {
            IReadOnlyDictionary<int, List<(string Key, string Display)>> buckets = list.ByLength();

            for (int length = key.Length - maxDistance; length <= key.Length + maxDistance; length++)
            {
                if (!buckets.TryGetValue(length, out var bucket)) continue;

                foreach ((string candidateKey, string display) in bucket)
                {
                    int distance = TextUtil.BoundedDistance(key, candidateKey, maxDistance);
                    if (distance == 0 || distance > maxDistance || distance > bestDistance) continue;

                    int used = frequency?.Invoke(display) ?? 0;
                    bool better = best == null || distance < bestDistance || used > bestFrequency
                        || (used == bestFrequency && string.CompareOrdinal(display, best) < 0);

                    if (distance == bestDistance && !better) continue;

                    best = display;
                    bestDistance = distance;
                    bestFrequency = used;
                }
            }
        }

        return best == null ? null : (best, bestDistance);
    }

    /// <summary>Comparison key: lower case, every non letter / digit is a space, spaces collapsed.</summary>
    internal static string Key(string value)
    {
        StringBuilder sb = new(value.Length);
        bool pendingSpace = false;

        foreach (char c in value)
        {
            if (char.IsLetterOrDigit(c) || char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                if (pendingSpace && sb.Length > 0) sb.Append(' ');
                pendingSpace = false;
                sb.Append(char.ToLowerInvariant(c));
            }
            else if (!TextUtil.IsInvisible(c) || char.IsWhiteSpace(c))
            {
                pendingSpace = true;
            }
        }

        return sb.ToString();
    }
}
