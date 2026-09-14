using System.Data;
using GeneralMaintenanceManager.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace GeneralMaintenanceManager.Infrastructure.Services;

/// <summary>
/// Transactional permanent-reference allocator. Sequence rows are authoritative;
/// clean development databases are never rebuilt from historical records.
/// </summary>
internal static class ReferenceNumberAllocator
{
    public static async Task<string> ReserveMaintenanceAsync(MasterDbContext context, int year, CancellationToken cancellationToken)
    {
        var value = await ReserveAsync(context, year, "MaintenanceNumberSequence", cancellationToken).ConfigureAwait(false);
        if (value > 9_999_999)
            throw new InvalidOperationException($"Maintenance reference capacity for {year} has been exhausted.");
        return MaintenanceReference.Format(year, value);
    }

    public static async Task<string> ReserveWorkOrderAsync(MasterDbContext context, int year, CancellationToken cancellationToken)
    {
        var value = await ReserveAsync(context, year, "WorkOrderNumberSequence", cancellationToken).ConfigureAwait(false);
        if (value > 999_999) throw new InvalidOperationException($"Work Order reference capacity for {year} has been exhausted.");
        return $"WO-{year:0000}-{value:000000}";
    }

    public static async Task<string> ReservePreventiveAsync(MasterDbContext context, int year, CancellationToken cancellationToken)
    {
        var value = await ReserveAsync(context, year, "MaintenancePlanNumberSequence", cancellationToken).ConfigureAwait(false);
        if (value > 999_999) throw new InvalidOperationException($"Preventive Maintenance reference capacity for {year} has been exhausted.");
        return $"PM-{year:0000}-{value:000000}";
    }

    private static async Task<long> ReserveAsync(
        MasterDbContext context,
        int year,
        string table,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        DataPaths.ValidateYear(year);
        if (context.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Reference allocation requires an active master-database transaction.");

        var connection = context.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = context.Database.CurrentTransaction.GetDbTransaction();
        command.CommandText = table switch
        {
            "MaintenanceNumberSequence" =>
                "INSERT INTO MaintenanceNumberSequence (Year, LastValue) VALUES ($year, 1) " +
                "ON CONFLICT(Year) DO UPDATE SET LastValue = LastValue + 1 RETURNING LastValue;",
            "WorkOrderNumberSequence" =>
                "INSERT INTO WorkOrderNumberSequence (Year, LastValue) VALUES ($year, 1) " +
                "ON CONFLICT(Year) DO UPDATE SET LastValue = LastValue + 1 RETURNING LastValue;",
            "MaintenancePlanNumberSequence" =>
                "INSERT INTO MaintenancePlanNumberSequence (Year, LastValue) VALUES ($year, 1) " +
                "ON CONFLICT(Year) DO UPDATE SET LastValue = LastValue + 1 RETURNING LastValue;",
            _ => throw new ArgumentOutOfRangeException(nameof(table))
        };
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$year";
        parameter.Value = year;
        command.Parameters.Add(parameter);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
    }
}
