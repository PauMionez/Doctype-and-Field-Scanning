using System.Text;

namespace Doctype_Scanning.Services;

internal static class TextUtil
{
    /// <summary>Zero-width / non-breaking / control characters that look like nothing (or a space) but are not.</summary>
    public static bool IsInvisible(char c) =>
        c is ' ' or '​' or '‌' or '‍' or '⁠' or '﻿' || char.IsControl(c);

    private static bool IsZeroWidth(char c) =>
        c is '​' or '‌' or '‍' or '⁠' or '﻿';

    /// <summary>Drops zero-width characters, turns every whitespace run (NBSP and tabs included) into one space and trims.</summary>
    public static string Clean(string input)
    {
        StringBuilder sb = new(input.Length);
        bool pendingSpace = false;

        foreach (char c in input)
        {
            if (IsZeroWidth(c)) continue;

            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>Same text with invisible characters spelled out, so the excel reader can see what is wrong.</summary>
    public static string Visible(string input)
    {
        StringBuilder sb = new(input.Length);

        foreach (char c in input)
        {
            if (IsInvisible(c)) sb.Append("<U+").Append(((int)c).ToString("X4")).Append('>');
            else sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>Case-insensitive comparison key for doctype names.</summary>
    public static string Loose(string input) => Clean(input).ToLowerInvariant();

    /// <summary>
    /// Damerau-Levenshtein (optimal string alignment) distance that gives up once it is certain to exceed <paramref name="max"/>.
    /// Returns max + 1 in that case.
    /// </summary>
    public static int BoundedDistance(string a, string b, int max)
    {
        int n = a.Length, m = b.Length;
        if (Math.Abs(n - m) > max) return max + 1;

        Span<int> prev2 = m < 128 ? stackalloc int[m + 1] : new int[m + 1];
        Span<int> prev = m < 128 ? stackalloc int[m + 1] : new int[m + 1];
        Span<int> cur = m < 128 ? stackalloc int[m + 1] : new int[m + 1];

        for (int j = 0; j <= m; j++) prev[j] = j;

        for (int i = 1; i <= n; i++)
        {
            cur[0] = i;
            int rowMin = i;

            for (int j = 1; j <= m; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                int v = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + cost);

                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                    v = Math.Min(v, prev2[j - 2] + 1);

                cur[j] = v;
                if (v < rowMin) rowMin = v;
            }

            if (rowMin > max) return max + 1;

            Span<int> recycled = prev2;
            prev2 = prev;
            prev = cur;
            cur = recycled;
        }

        return prev[m];
    }
}
