namespace GeneralMaintenanceManager.Infrastructure.Data;

/// <summary>
/// Registered forward-only production migrations for the current release.
/// The v1 production baseline has no upgrade steps yet; future releases add one
/// contiguous step per supported version edge and increment ProductionSchemaPolicy.CurrentVersion.
/// </summary>
internal static class ProductionMigrationCatalog
{
    public static IReadOnlyCollection<ProductionMigrationStep> MasterSteps { get; } = [];
    public static IReadOnlyCollection<ProductionMigrationStep> AnnualSteps { get; } = [];
}
