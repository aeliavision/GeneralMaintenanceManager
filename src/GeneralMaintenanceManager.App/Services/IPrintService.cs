using GeneralMaintenanceManager.App.ViewModels;
using GeneralMaintenanceManager.Core.Entities;

namespace GeneralMaintenanceManager.App.Services;

public interface IPrintService
{
    bool PrintAssetHistory(Asset asset, IReadOnlyList<MaintenanceRecord> records);
    bool PrintWorkOrder(WorkOrder workOrder, Asset? asset, bool useDefaultPrinter = false);
    bool PrintMaintenanceRecord(
        MaintenanceRecord record,
        IReadOnlyList<MaintenanceActivity> activities,
        WorkOrder? sourceWorkOrder = null);
    bool PrintMaintenancePlan(MaintenancePlan plan, IReadOnlyList<ActivityHistoryEntry> activities);
    bool PrintGlobalHistory(IReadOnlyList<GlobalHistoryItemViewModel> items, string scope);
}
