using System;
using System.Globalization;

namespace TVHeadEnd.Helper;

internal static class HtspFieldHelper
{
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
