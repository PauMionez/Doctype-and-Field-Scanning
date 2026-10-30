using Doctype_Scanning.Models;
using System.Globalization;

namespace Doctype_Scanning.Services.Qc;

/// <summary>DocType checks 10, 11 and 12 (fields that do not belong to the doctype, Capture misuse, Capture share).</summary>
internal static class QcDoctypeChecks
{
    // fields that must be empty on a Coverpage / on a Census image
    private static readonly string[] CoverpageForbidden =
        { QcFields.LineNumber, QcFields.Age, QcFields.Gender, QcFields.StreetAddress };

    private static readonly string[] CensusForbidden =
        { QcFields.NumberOfMales, QcFields.NumberOfFemales, QcFields.TotalPersons, QcFields.RoomsOccupied };

    public static List<QcFlag> Run(IReadOnlyList<QcImage> images, double captureLimitPercent)
    {
        List<QcFlag> flags = new();

        WrongFields(images, flags);
        CaptureUse(images, flags);
        CaptureShare(images, captureLimitPercent, flags);

        return flags;
    }

    /// <summary>Check 10: a Coverpage with Line Number / Age / Gender / Street Address, or a Census with Number of Males / Females / Total Persons / Rooms.</summary>
    private static void WrongFields(IReadOnlyList<QcImage> images, List<QcFlag> flags)
    {
        foreach (QcImage image in images)
        {
            string[] forbidden = QcDoctypes.Is(image, QcDoctypes.Coverpage) ? CoverpageForbidden
                : QcDoctypes.Is(image, QcDoctypes.Census) ? CensusForbidden
                : Array.Empty<string>();

            foreach (string field in forbidden)
            {
                string value = image.Value(field);
                if (TextUtil.Clean(value).Length == 0) continue;

                flags.Add(new QcFlag(image.Name, field, value, 10, $"Field '{field}' should not be keyed on a {image.DocType} image", string.Empty));
            }
        }
    }

    /// <summary>Check 11: a Capture image with keyed data, or a Capture inside a run of data images (right after a Coverpage is normal).</summary>
    private static void CaptureUse(IReadOnlyList<QcImage> images, List<QcFlag> flags)
    {
        for (int i = 0; i < images.Count; i++)
        {
            QcImage image = images[i];
            if (!QcDoctypes.Is(image, QcDoctypes.Capture)) continue;

            if (image.HasKeyedData)
            {
                flags.Add(new QcFlag(image.Name, "DocType", image.DocType, 11,
                    image.RecordCount > 0 ? $"Capture image has keyed data ({image.RecordCount} record(s))" : "Capture image has keyed data",
                    string.Empty));
            }

            bool afterCensus = i > 0 && QcDoctypes.Is(images[i - 1], QcDoctypes.Census);
            bool beforeData = i < images.Count - 1 && QcDoctypes.IsData(images[i + 1]);

            if (afterCensus && beforeData)
            {
                flags.Add(new QcFlag(image.Name, "DocType", image.DocType, 11,
                    $"Capture inside a run of data images (after Census {images[i - 1].Name}, before {images[i + 1].DocType} {images[i + 1].Name})",
                    string.Empty));
            }
        }
    }

    /// <summary>Check 12: the share of Capture images in the batch is above the limit.</summary>
    private static void CaptureShare(IReadOnlyList<QcImage> images, double limitPercent, List<QcFlag> flags)
    {
        if (images.Count == 0) return;

        int captures = images.Count(i => QcDoctypes.Is(i, QcDoctypes.Capture));
        double share = captures * 100.0 / images.Count;
        if (share <= limitPercent) return;

        string text = $"{captures} of {images.Count} images ({share.ToString("0.#", CultureInfo.InvariantCulture)}%)";
        flags.Add(new QcFlag(string.Empty, "DocType", text, 12,
            $"Capture share {share.ToString("0.#", CultureInfo.InvariantCulture)}% is above the {limitPercent.ToString("0.#", CultureInfo.InvariantCulture)}% limit",
            string.Empty));
    }
}
