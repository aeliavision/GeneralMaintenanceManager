namespace GeneralMaintenanceManager.Core.Abstractions;

public interface IDataHealthService
{
    public Task ValidateStartupDataAsync(CancellationToken cancellationToken = default);

    public Task ValidateCurrentDataAsync(CancellationToken cancellationToken = default);
}
