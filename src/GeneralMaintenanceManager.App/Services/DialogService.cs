using System.IO;
using System.Windows;
using System.Windows.Markup;
using GeneralMaintenanceManager.App.Models;
using GeneralMaintenanceManager.App.ViewModels;
using GeneralMaintenanceManager.App.Views;
using GeneralMaintenanceManager.Core.Entities;
using GeneralMaintenanceManager.Core.Models;
using Microsoft.Win32;

namespace GeneralMaintenanceManager.App.Services;

public sealed class DialogService(
    ILocalizationService localization,
    IPrintService printService,
    IUserSettingsService userSettingsService) : IDialogService
{
    public MaintenanceRecordEditDraft? ShowMaintenanceRecordEditDialog(MaintenanceEntryOptions options, MaintenanceRecord record)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(record);
        var viewModel = new MaintenanceRecordDialogViewModel(options, record, localization);
        var dialog = new MaintenanceRecordDialog(viewModel);
        ConfigureDialogOwner(dialog);
        return dialog.ShowDialog() == true ? dialog.EditResult : null;
    }

    public MaintenanceRecordDetailsAction ShowMaintenanceRecordDetailsDialog(MaintenanceRecord record, IReadOnlyList<MaintenanceActivity> activities)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(activities);
        var viewModel = new MaintenanceRecordDetailsDialogViewModel(record, activities, localization);
        var dialog = new MaintenanceRecordDetailsDialog(viewModel);
        ConfigureDialogOwner(dialog);
        dialog.ShowDialog();
        return dialog.Action;
    }

    public WorkOrderDraft? ShowMaintenanceOrderDialog(MaintenanceEntryOptions options, Asset? preferredAsset)
    {
        ArgumentNullException.ThrowIfNull(options);
        var viewModel = new MaintenanceOrderDialogViewModel(options, preferredAsset, localization);
        var dialog = new MaintenanceOrderDialog(viewModel);
        ConfigureDialogOwner(dialog);
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    public WorkOrderDraft? ShowWorkOrderDialog(IReadOnlyList<Asset> assets, Asset? preferredAsset)
    {
        var viewModel = new WorkOrderDialogViewModel(assets, preferredAsset, localization);
        var dialog = new WorkOrderDialog(viewModel);
        ConfigureDialogOwner(dialog);
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    public WorkOrderDraft? ShowWorkOrderEditDialog(IReadOnlyList<Asset> assets, WorkOrder workOrder)
    {
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(workOrder);
        var viewModel = new WorkOrderDialogViewModel(assets, workOrder, localization);
        var dialog = new WorkOrderDialog(viewModel);
        ConfigureDialogOwner(dialog);
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    public WorkOrderDetailsAction ShowWorkOrderDetailsDialog(
        WorkOrder workOrder,
        Asset? asset,
        MaintenanceRecord? generatedMaintenanceRecord,
        IReadOnlyList<ActivityHistoryEntry> activities)
    {
        ArgumentNullException.ThrowIfNull(workOrder);
        ArgumentNullException.ThrowIfNull(activities);
        var viewModel = new WorkOrderDetailsDialogViewModel(workOrder, asset, generatedMaintenanceRecord, activities, localization);
        var dialog = new WorkOrderDetailsDialog(viewModel, printService, workOrder, asset, localization);
        ConfigureDialogOwner(dialog);
        dialog.ShowDialog();
        return dialog.Action;
    }

    public WorkOrderCompletionDraft? ShowWorkOrderCompletionDialog(WorkOrder workOrder, Asset? asset)
    {
        ArgumentNullException.ThrowIfNull(workOrder);
        var viewModel = new WorkOrderCompletionDialogViewModel(workOrder, asset, localization);
        var dialog = new WorkOrderCompletionDialog(viewModel);
        ConfigureDialogOwner(dialog);
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    public MaintenancePlanDraft? ShowMaintenancePlanDialog(IReadOnlyList<Asset> assets, Asset? preferredAsset)
    {
        var viewModel = new MaintenancePlanDialogViewModel(assets, preferredAsset, userSettingsService.LoadAssetTrackingEnabled(), localization);
        var dialog = new MaintenancePlanDialog(viewModel);
        ConfigureDialogOwner(dialog);
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    public MaintenancePlanDraft? ShowMaintenancePlanEditDialog(IReadOnlyList<Asset> assets, MaintenancePlan maintenancePlan)
    {
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(maintenancePlan);
        var viewModel = new MaintenancePlanDialogViewModel(assets, maintenancePlan, userSettingsService.LoadAssetTrackingEnabled(), localization);
        var dialog = new MaintenancePlanDialog(viewModel);
        ConfigureDialogOwner(dialog);
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    public AssetDialogResult? ShowAssetDialog(Asset? asset)
    {
        var viewModel = new AssetDialogViewModel(asset, localization);
        var dialog = new AssetDialog(viewModel, localization, asset);
        ConfigureDialogOwner(dialog);

        if (dialog.ShowDialog() != true)
        {
            return null;
        }

        return dialog.DeleteRequested
            ? AssetDialogResult.Delete()
            : dialog.Result is not null
                ? AssetDialogResult.Save(dialog.Result)
                : null;
    }

    public AssetNumberChangeDraft? ShowAssetNumberDialog(Asset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        var viewModel = new AssetNumberDialogViewModel(asset, localization);
        var dialog = new AssetNumberDialog(viewModel);
        ConfigureDialogOwner(dialog);
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    public void ShowAuditDialog(Asset? asset, MaintenanceRecord record, IReadOnlyList<MaintenanceActivity> activities)
    {
        var viewModel = new AuditDialogViewModel(asset, record, activities, localization);
        var dialog = new AuditDialog(viewModel);
        ConfigureDialogOwner(dialog);
        dialog.ShowDialog();
    }


    public void ShowReportItemDetailsDialog(GlobalHistoryItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var dialog = new ReportItemDetailsDialog(item, printService, localization);
        ConfigureDialogOwner(dialog);
        dialog.ShowDialog();
    }

    public string? ShowRequiredTextPrompt(string title, string label, string requiredMessage, string initialValue = "")
    {
        var viewModel = new TextPromptDialogViewModel(title, label, initialValue);
        var dialog = new TextPromptDialog(viewModel, requiredMessage);
        ConfigureDialogOwner(dialog);
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    public string? PickExcelFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = localization.GetString("OpenFileImportTitle"),
            Filter = localization.GetString("OpenFileExcelFilter"),
            CheckFileExists = true,
            Multiselect = false
        };

        return ShowOpenDialog(dialog);
    }

    public string? PickBackupFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = localization.GetString("RestoreBackupPickerTitle"),
            Filter = localization.GetString("BackupFileFilter"),
            CheckFileExists = true,
            Multiselect = false
        };

        return ShowOpenDialog(dialog);
    }

    public string? PickAssetExportExcelPath(string suggestedFileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(suggestedFileName);
        var dialog = new SaveFileDialog
        {
            Title = localization.GetString("ExportAssetPickerTitle"),
            Filter = localization.GetString("ExportExcelFileFilter"),
            DefaultExt = ".xlsx",
            AddExtension = true,
            OverwritePrompt = true,
            CheckPathExists = true,
            FileName = suggestedFileName
        };

        return ShowSaveDialog(dialog);
    }

    public string? PickAssetExportCsvPath(string suggestedFileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(suggestedFileName);
        var dialog = new SaveFileDialog
        {
            Title = localization.GetString("ExportAssetPickerTitle"),
            Filter = localization.GetString("ExportCsvFileFilter"),
            DefaultExt = ".csv",
            AddExtension = true,
            OverwritePrompt = true,
            CheckPathExists = true,
            FileName = suggestedFileName
        };

        return ShowSaveDialog(dialog);
    }

    public string? PickBackupSavePath(string initialDirectory, string suggestedFileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(initialDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(suggestedFileName);

        Directory.CreateDirectory(initialDirectory);

        var dialog = new SaveFileDialog
        {
            Title = localization.GetString("CreateBackupPickerTitle"),
            Filter = localization.GetString("BackupFileFilter"),
            DefaultExt = ".zip",
            AddExtension = true,
            OverwritePrompt = true,
            CheckPathExists = true,
            InitialDirectory = initialDirectory,
            FileName = suggestedFileName
        };

        return ShowSaveDialog(dialog);
    }

    public bool Confirm(string title, string message)
    {
        return ModernMessageDialog.ShowConfirmation(
            GetVisibleMainWindow(),
            title,
            message,
            localization.CurrentCulture);
    }

    public void ShowInformation(string title, string message)
    {
        ModernMessageDialog.ShowInformation(
            GetVisibleMainWindow(),
            title,
            message,
            localization.CurrentCulture);
    }

    public void ShowError(string title, string message)
    {
        ModernMessageDialog.ShowError(
            GetVisibleMainWindow(),
            title,
            message,
            localization.CurrentCulture);
    }

    private static string? ShowOpenDialog(OpenFileDialog dialog)
    {
        var owner = GetVisibleMainWindow();
        return owner is null
            ? dialog.ShowDialog() == true ? dialog.FileName : null
            : dialog.ShowDialog(owner) == true ? dialog.FileName : null;
    }

    private static string? ShowSaveDialog(SaveFileDialog dialog)
    {
        var owner = GetVisibleMainWindow();
        return owner is null
            ? dialog.ShowDialog() == true ? dialog.FileName : null
            : dialog.ShowDialog(owner) == true ? dialog.FileName : null;
    }

    private void ConfigureDialogOwner(Window dialog)
    {
        dialog.FlowDirection = localization.CurrentCulture.TextInfo.IsRightToLeft
            ? FlowDirection.RightToLeft
            : FlowDirection.LeftToRight;
        dialog.Language = XmlLanguage.GetLanguage(localization.CurrentCulture.IetfLanguageTag);

        var owner = GetVisibleMainWindow();
        if (owner is null || ReferenceEquals(owner, dialog))
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return;
        }

        dialog.Owner = owner;
        dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
    }

    private static Window? GetVisibleMainWindow()
    {
        var application = Application.Current;
        var mainWindow = application?.MainWindow;
        return mainWindow is { IsLoaded: true, IsVisible: true } ? mainWindow : null;
    }
}
