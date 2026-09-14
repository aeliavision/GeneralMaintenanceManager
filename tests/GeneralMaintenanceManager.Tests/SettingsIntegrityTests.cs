using GeneralMaintenanceManager.App.Services;
using GeneralMaintenanceManager.Infrastructure.Data;

namespace GeneralMaintenanceManager.Tests;

[TestClass]
public sealed class SettingsIntegrityTests
{
    [TestMethod]
    public void AssetTrackingDefaultsOffAndPersists()
    {
        using var scope = new TestDataScope();
        var service = new UserSettingsService(scope.Paths);
        Assert.IsFalse(service.LoadAssetTrackingEnabled());
        service.SaveAssetTrackingEnabled(true);
        Assert.IsTrue(service.LoadAssetTrackingEnabled());
        service.SaveAssetTrackingEnabled(false);
        Assert.IsFalse(service.LoadAssetTrackingEnabled());
    }
}
