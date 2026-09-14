using System.Globalization;
using System.Text.RegularExpressions;

namespace GeneralMaintenanceManager.Infrastructure.Data;

public static partial class MaintenanceReference
{
    [GeneratedRegex("^MNT-(?<year>[0-9]{4})-(?<sequence>[0-9]{7})$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex Pattern();

    public static string Format(int year, long sequence)
    {
        DataPaths.ValidateYear(year);
        if (sequence is < 1 or > 9_999_999) throw new ArgumentOutOfRangeException(nameof(sequence));
        return string.Create(CultureInfo.InvariantCulture, $"MNT-{year:0000}-{sequence:0000000}");
    }

    public static bool TryParse(string? value, out int year, out long sequence)
    {
        year = 0;
        sequence = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var match = Pattern().Match(value.Trim());
        return match.Success
            && int.TryParse(match.Groups["year"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out year)
            && long.TryParse(match.Groups["sequence"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out sequence)
            && year is >= 1900 and <= 9999
            && sequence is >= 1 and <= 9_999_999;
    }
}
