using GeneralMaintenanceManager.App.Models;
using GeneralMaintenanceManager.App.ViewModels;
using GeneralMaintenanceManager.Core.Entities;
using GeneralMaintenanceManager.Core.Models;

namespace GeneralMaintenanceManager.App.Services;

public interface IDialogService
{
    public MaintenanceRecordEditDraft? ShowMaintenanceRecordEditDialog(MaintenanceEntryOptions options, MaintenanceRecord record);

    public MaintenanceRecordDetailsAction ShowMaintenanceRecordDetailsDialog(MaintenanceRecord record, IReadOnlyList<MaintenanceActivity> activities);

    public AssetDialogResult? ShowAssetDialog(Asset? asset);

    public WorkOrderDraft? ShowMaintenanceOrderDialog(MaintenanceEntryOptions options, Asset? preferredAsset);

    public WorkOrderDraft? ShowWorkOrderDialog(IReadOnlyList<Asset> assets, Asset? preferredAsset);

    public WorkOrderDraft? ShowWorkOrderEditDialog(IReadOnlyList<Asset> assets, WorkOrder workOrder);

    public WorkOrderDetailsAction ShowWorkOrderDetailsDialog(
        WorkOrder workOrder,
        Asset? asset,
        MaintenanceRecord? generatedMaintenanceRecord,
        IReadOnlyList<ActivityHistoryEntry> activities);

    public WorkOrderCompletionDraft? ShowWorkOrderCompletionDialog(WorkOrder workOrder, Asset? asset);

    public MaintenancePlanDraft? ShowMaintenancePlanDialog(IReadOnlyList<Asset> assets, Asset? preferredAsset);

    public MaintenancePlanDraft? ShowMaintenancePlanEditDialog(IReadOnlyList<Asset> assets, MaintenancePlan maintenancePlan);

    public AssetNumberChangeDraft? ShowAssetNumberDialog(Asset asset);
    public void ShowAuditDialog(Asset? asset, MaintenanceRecord record, IReadOnlyList<MaintenanceActivity> activities);


    public void ShowReportItemDetailsDialog(GlobalHistoryItemViewModel item);

    public string? ShowRequiredTextPrompt(string title, string label, string requiredMessage, string initialValue = "");

    public string? PickExcelFile();

    public string? PickBackupFile();

    public string? PickAssetExportExcelPath(string suggestedFileName);

    public string? PickAssetExportCsvPath(string suggestedFileName);

    public string? PickBackupSavePath(string initialDirectory, string suggestedFileName);

    public bool Confirm(string title, string message);

    public void ShowInformation(string title, string message);

    public void ShowError(string title, string message);
}
