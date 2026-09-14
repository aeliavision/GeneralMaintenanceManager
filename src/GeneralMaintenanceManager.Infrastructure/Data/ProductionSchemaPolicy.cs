namespace GeneralMaintenanceManager.Infrastructure.Data;

/// <summary>
/// DB-9 release boundary. Versions 2-7 used before this boundary were disposable
/// development identities and are not part of the production migration chain.
/// The first customer-preserving General Maintenance Manager database contract is v1.
/// </summary>
public static class ProductionSchemaPolicy
{
    public const int BaselineVersion = 1;
    public const int CurrentVersion = 1;
}
