using System;
using System.Globalization;
using System.Linq;

namespace TVHeadEnd.Helper;

internal static class HtspFieldHelper
{
    public static string NormalizeLanguage(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim().ToLowerInvariant();
        if (value.Length == 3 && value.All(c => c >= 'a' && c <= 'z'))
        {
            // Preserve broadcast ISO-639 aliases; "und" supplies no language information.
            return value == "und" ? null : value;
        }

        var primary = value.Split('-', '_')[0];
        if (primary.Length != 2 || !primary.All(c => c >= 'a' && c <= 'z')) return null;
        try
        {
            var iso = CultureInfo.GetCultureInfo(value.Replace('_', '-')).ThreeLetterISOLanguageName;
            return iso.Length == 3 ? iso : null;
        }
        catch (CultureNotFoundException)
        {
            return null;
        }
    }

    public static long ParseUInt32Id(string value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"HTSP field '{fieldName}' requires a non-empty unsigned 32-bit numeric identifier.", nameof(value));
        }

        if (!uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out uint parsed))
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, $"HTSP field '{fieldName}' requires an unsigned 32-bit numeric identifier.");
        }

        return parsed;
    }
}
