using System.Globalization;

namespace GeneralMaintenanceManager.Infrastructure.Data;

internal static class WorkOrderReference
{
    public static bool TryParse(string? value, out int year, out long sequence)
    {
        year = 0;
        sequence = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var parts = value.Trim().Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 3
            && string.Equals(parts[0], "WO", StringComparison.OrdinalIgnoreCase)
            && parts[1].Length == 4
            && long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out sequence)
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out year)
            && year is >= 1900 and <= 9999
            && sequence is >= 1 and <= 999_999;
    }
}
