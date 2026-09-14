namespace GeneralMaintenanceManager.App.Models;

public static class CustomMaintenanceTypeStorage
{
    private const string Prefix = "custom-maintenance-type:";

    public static string Encode(string? label)
    {
        var normalized = Normalize(label);
        return normalized.Length == 0 ? string.Empty : Prefix + normalized;
    }

    public static bool TryDecode(string? storedValue, out string label)
    {
        label = string.Empty;
        if (string.IsNullOrWhiteSpace(storedValue)
            || !storedValue.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        label = Normalize(storedValue[Prefix.Length..]);
        return label.Length > 0;
    }

    public static string Normalize(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length > 64) normalized = normalized[..64].Trim();
        return normalized;
    }
}
