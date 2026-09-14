using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GeneralMaintenanceManager.Tests;

[TestClass]
public sealed class WorkOrderWorkspaceUiTests
{
    [TestMethod]
    public void WorkOrdersWorkspaceOwnsOpenCompletedCancelledAndAllViewsWithoutPendingDuplicate()
    {
        var mainWindow = ReadContract("MainWindow.xaml");
        var workOrdersView = ReadContract("WorkOrdersView.xaml");
        var mainViewModel = ReadContract("MainViewModel.cs");
        var workOrdersFeature = ReadContract("MainViewModel.WorkOrders.cs");
        var maintenanceFeature = ReadContract("MainViewModel.Maintenance.cs");
        var preventiveFeature = ReadContract("MainViewModel.PreventiveMaintenance.cs");

        Assert.IsFalse(mainWindow.Contains("ShowPendingMaintenanceOrdersCommand", StringComparison.Ordinal));
        Assert.IsFalse(mainWindow.Contains("PendingMaintenanceOrdersView", StringComparison.Ordinal));
        Assert.IsTrue(mainWindow.Contains("ShowWorkOrdersCommand", StringComparison.Ordinal));
        Assert.IsTrue(mainWindow.Contains("OpenWorkOrderCount", StringComparison.Ordinal));

        StringAssert.Contains(workOrdersView, "WorkOrderTabOpen");
        StringAssert.Contains(workOrdersView, "WorkOrderTabCompleted");
        StringAssert.Contains(workOrdersView, "WorkOrderTabCancelled");
        StringAssert.Contains(workOrdersView, "WorkOrderTabAll");
        StringAssert.Contains(workOrdersView, "SetWorkOrderListModeCommand");

        Assert.IsFalse(mainViewModel.Contains("IsPendingMaintenanceOrdersView", StringComparison.Ordinal));
        StringAssert.Contains(mainViewModel, "SetWorkOrderListModeCommand");
        StringAssert.Contains(workOrdersFeature, "WorkOrderStatus.Completed");
        StringAssert.Contains(workOrdersFeature, "WorkOrderStatus.Cancelled");
        StringAssert.Contains(workOrdersFeature, "IsClosed: IsWorkOrderListOpen ? false : null");
        StringAssert.Contains(maintenanceFeature, "SetWorkOrderListMode(\"Open\")");
        StringAssert.Contains(preventiveFeature, "SetWorkOrderListMode(\"Open\")");
    }

    private static string ReadContract(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "UiContracts", fileName);
        Assert.IsTrue(File.Exists(path), $"UI contract source was not copied: {path}");
        return File.ReadAllText(path);
    }
}
