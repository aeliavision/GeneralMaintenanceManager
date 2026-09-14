using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace GeneralMaintenanceManager.Infrastructure.Data;

/// <summary>
/// Exact SQLite numeric policy. Currency is persisted as integer minor units (scale 100),
/// and downtime is persisted as whole minutes. Public/domain APIs remain decimal?.
/// </summary>
internal static class ExactNumericStorage
{
    public const decimal MoneyScale = 100m;
    public const decimal MinutesPerHour = 60m;
    // DB-10 certifies up to one million canonical Maintenance rows per annual partition.
    // Capping each stored money value to this exact integer share guarantees that even a
    // worst-case SQLite SUM over a certified partition cannot overflow signed Int64.
    public const long CertifiedMaximumRowsPerAnnualPartition = 1_000_000;
    public const long MaximumMoneyMinorUnitsPerRecord = long.MaxValue / CertifiedMaximumRowsPerAnnualPartition;

    public static ValueConverter<decimal?, long?> MoneyConverter { get; } = new(
        value => ToMinorUnits(value),
        value => FromMinorUnits(value));

    public static ValueConverter<decimal?, long?> HoursToMinutesConverter { get; } = new(
        value => ToMinutes(value),
        value => FromMinutes(value));

    public static long? ToMinorUnits(decimal? value)
    {
        if (!value.HasValue) return null;
        ValidateMoney(value, nameof(value));
        return checked(decimal.ToInt64(value.Value * MoneyScale));
    }

    public static decimal? FromMinorUnits(long? value) => value.HasValue ? value.Value / MoneyScale : null;

    public static long? ToMinutes(decimal? hours)
    {
        if (!hours.HasValue) return null;
        ValidateDowntimeHours(hours, nameof(hours));
        return checked(decimal.ToInt64(hours.Value * MinutesPerHour));
    }

    public static decimal? FromMinutes(long? minutes) => minutes.HasValue ? minutes.Value / MinutesPerHour : null;

    public static void ValidateMoney(decimal? value, string paramName)
    {
        if (!value.HasValue) return;
        if (value.Value < 0) throw new ArgumentOutOfRangeException(paramName, value, "Money values cannot be negative.");
        var maximumMoney = MaximumMoneyMinorUnitsPerRecord / MoneyScale;
        if (value.Value > maximumMoney)
            throw new ArgumentOutOfRangeException(paramName, value,
                $"Money value exceeds the per-record exact-integer limit required for safe aggregation of {CertifiedMaximumRowsPerAnnualPartition:N0} annual records.");
        var scaled = value.Value * MoneyScale;
        if (scaled != decimal.Truncate(scaled))
            throw new ArgumentException("Money values support at most two decimal places.", paramName);
    }

    public static void ValidateDowntimeHours(decimal? value, string paramName)
    {
        if (!value.HasValue) return;
        if (value.Value < 0) throw new ArgumentOutOfRangeException(paramName, value, "Downtime cannot be negative.");
        var minutes = value.Value * MinutesPerHour;
        if (minutes != decimal.Truncate(minutes))
            throw new ArgumentException("Downtime must resolve to a whole number of minutes.", paramName);
        if (minutes > long.MaxValue)
            throw new ArgumentOutOfRangeException(paramName, value, "Downtime exceeds the supported SQLite integer range.");
    }
}
