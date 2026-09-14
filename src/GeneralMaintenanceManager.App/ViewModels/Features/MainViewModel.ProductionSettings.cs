using System.Globalization;
using Microsoft.Win32;
using CommunityToolkit.Mvvm.Input;
using GeneralMaintenanceManager.App.Models;
using GeneralMaintenanceManager.App.Services;
using GeneralMaintenanceManager.Core.Entities;

namespace GeneralMaintenanceManager.App.ViewModels;

public sealed partial class MainViewModel
{
    private PrintSettingsEditor _printSettingsEditor = PrintSettingsEditor.FromSettings(PrintSettings.Default);
    private string _databaseMaintenanceStatusText = string.Empty;
    private string _databaseMaintenanceDetails = string.Empty;
    private bool _isDatabaseMaintenanceRunning;

    public PrintSettingsEditor PrintSettingsEditor
    {
        get => _printSettingsEditor;
        private set => SetProperty(ref _printSettingsEditor, value);
    }

    public IReadOnlyList<string> PrintBrandingModes { get; } =
        [PrintCenterBrandingMode.Logo.ToString(), PrintCenterBrandingMode.Text.ToString()];

    public string DatabaseMaintenanceStatusText
    {
        get => _databaseMaintenanceStatusText;
        private set => SetProperty(ref _databaseMaintenanceStatusText, value);
    }

    public string DatabaseMaintenanceDetails
    {
        get => _databaseMaintenanceDetails;
        private set => SetProperty(ref _databaseMaintenanceDetails, value);
    }

    public bool IsDatabaseMaintenanceRunning
    {
        get => _isDatabaseMaintenanceRunning;
        private set => SetProperty(ref _isDatabaseMaintenanceRunning, value);
    }

    public IRelayCommand SavePrintSettingsCommand { get; private set; } = null!;
    public IRelayCommand ResetPrintSettingsCommand { get; private set; } = null!;
    public IRelayCommand ChoosePrintLogoCommand { get; private set; } = null!;
    public IRelayCommand RemovePrintLogoCommand { get; private set; } = null!;
    public IAsyncRelayCommand RefreshDatabaseStatusCommand { get; private set; } = null!;
    public IAsyncRelayCommand OptimizeDatabaseCommand { get; private set; } = null!;
    public IAsyncRelayCommand AnalyzeDatabaseCommand { get; private set; } = null!;
    public IAsyncRelayCommand FullIntegrityCheckCommand { get; private set; } = null!;
    public IAsyncRelayCommand RebuildWorkOrderSearchCommand { get; private set; } = null!;
    public IAsyncRelayCommand RebuildActivityHistorySearchCommand { get; private set; } = null!;
    public IAsyncRelayCommand RebuildReviewDatabaseCommand { get; private set; } = null!;

    private void InitializeProductionSettings()
    {
        PrintSettingsEditor = PrintSettingsEditor.FromSettings(_userSettingsService.LoadPrintSettings());
        SavePrintSettingsCommand = new RelayCommand(SavePrintSettings);
        ResetPrintSettingsCommand = new RelayCommand(ResetPrintSettings);
        ChoosePrintLogoCommand = new RelayCommand(ChoosePrintLogo);
        RemovePrintLogoCommand = new RelayCommand(RemovePrintLogo);
        RefreshDatabaseStatusCommand = new AsyncRelayCommand(RefreshDatabaseStatusAsync, () => !IsBusy && !IsDatabaseMaintenanceRunning);
        OptimizeDatabaseCommand = new AsyncRelayCommand(
            () => RunDatabaseMaintenanceAsync(_databaseMaintenanceService.OptimizeAsync, "StatusDatabaseOptimizeCompleted"),
            () => !IsBusy && !IsDatabaseMaintenanceRunning);
        AnalyzeDatabaseCommand = new AsyncRelayCommand(
            () => RunDatabaseMaintenanceAsync(_databaseMaintenanceService.AnalyzeAndOptimizeAsync, "StatusDatabaseAnalyzeCompleted"),
            () => !IsBusy && !IsDatabaseMaintenanceRunning);
        FullIntegrityCheckCommand = new AsyncRelayCommand(
            () => RunDatabaseMaintenanceAsync(_databaseMaintenanceService.FullIntegrityCheckAsync, "StatusDatabaseIntegrityCompleted"),
            () => !IsBusy && !IsDatabaseMaintenanceRunning);
        RebuildWorkOrderSearchCommand = new AsyncRelayCommand(
            () => RunDatabaseMaintenanceAsync(_databaseMaintenanceService.RebuildWorkOrderSearchAsync, "StatusWorkOrderSearchRebuilt"),
            () => !IsBusy && !IsDatabaseMaintenanceRunning);
        RebuildActivityHistorySearchCommand = new AsyncRelayCommand(
            () => RunDatabaseMaintenanceAsync(_databaseMaintenanceService.RebuildActivityHistorySearchAsync, "StatusActivityHistorySearchRebuilt"),
            () => !IsBusy && !IsDatabaseMaintenanceRunning);
        RebuildReviewDatabaseCommand = new AsyncRelayCommand(
            () => RunDatabaseMaintenanceAsync(_reviewService.RefreshAsync, "StatusReviewRebuilt"),
            () => !IsBusy && !IsDatabaseMaintenanceRunning);
    }

    private void SavePrintSettings()
    {
        try
        {
            _userSettingsService.SavePrintSettings(PrintSettingsEditor.ToSettings());
            StatusText = _localization.GetString("StatusPrintSettingsSaved");
        }
        catch (Exception ex)
        {
            _diagnosticLogger.LogError("PrintSettings", ex, "Print settings could not be saved.");
            _dialogService.ShowError(_localization.GetString("PrintSettingsErrorTitle"), GetExceptionMessage(ex));
        }
    }

    private void ResetPrintSettings()
    {
        PrintSettingsEditor = PrintSettingsEditor.FromSettings(PrintSettings.Default);
        StatusText = _localization.GetString("StatusPrintSettingsReset");
    }

    private void ChoosePrintLogo()
    {
        var dialog = new OpenFileDialog
        {
            Title = _localization.GetString("PrintLogoPickerTitle"),
            Filter = _localization.GetString("PrintLogoFileFilter"),
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            PrintSettingsEditor.LogoPath = _userSettingsService.ImportPrintLogo(dialog.FileName);
            OnPropertyChanged(nameof(PrintSettingsEditor));
            StatusText = _localization.GetString("StatusPrintLogoSelected");
        }
        catch (Exception ex)
        {
            _dialogService.ShowError(_localization.GetString("PrintSettingsErrorTitle"), GetExceptionMessage(ex));
        }
    }

    private void RemovePrintLogo()
    {
        PrintSettingsEditor.LogoPath = null;
        OnPropertyChanged(nameof(PrintSettingsEditor));
        StatusText = _localization.GetString("StatusPrintLogoRemoved");
    }

    private async Task RefreshDatabaseStatusAsync()
    {
        try
        {
            using var lease = await _databaseActivityGate.EnterReadAsync().ConfigureAwait(true);
            var status = await _databaseMaintenanceService.GetStatusAsync().ConfigureAwait(true);
            DatabaseMaintenanceStatusText = _localization.GetString("DatabaseStatusReady");
            DatabaseMaintenanceDetails = string.Join(Environment.NewLine,
                _localization.Format("DatabaseSchemaStatusFormat", status.MasterSchemaVersion, status.AnnualSchemaVersion, status.AnnualDatabaseCount),
                _localization.Format("DatabaseSqliteStatusFormat", status.JournalMode, status.SynchronousMode),
                _localization.Format("DatabaseStorageStatusFormat", FormatBytes(status.DatabaseBytes), FormatBytes(status.WalBytes)),
                _localization.Format("DatabaseRecoveryStatusFormat", status.PendingWorkOrderCompletions),
                _localization.Format("DatabaseSearchStatusFormat", status.WorkOrderSearchRows, status.WorkOrderRows),
                _localization.Format("DatabasePreventiveStatusFormat", status.PreventiveOccurrenceRows));
        }
        catch (Exception ex)
        {
            DatabaseMaintenanceStatusText = _localization.GetString("DatabaseStatusUnavailable");
            DatabaseMaintenanceDetails = GetExceptionMessage(ex);
        }
    }

    private async Task RunDatabaseMaintenanceAsync(
        Func<CancellationToken, Task> operation,
        string successResourceKey)
    {
        IsBusy = true;
        IsDatabaseMaintenanceRunning = true;
        NotifyDatabaseMaintenanceCommands();
        StatusText = _localization.GetString("StatusDatabaseMaintenanceInProgress");
        try
        {
            _historyCancellation?.Cancel();
            _globalHistoryCancellation?.Cancel();
            using var lease = await _databaseActivityGate.EnterExclusiveAsync().ConfigureAwait(true);
            await operation(CancellationToken.None).ConfigureAwait(true);
            StatusText = _localization.GetString(successResourceKey);
        }
        catch (Exception ex)
        {
            _diagnosticLogger.LogError("DatabaseMaintenance", ex, "Explicit database maintenance operation failed.");
            StatusText = _localization.GetString("StatusDatabaseMaintenanceFailed");
            _dialogService.ShowError(_localization.GetString("DatabaseMaintenanceTab"), GetExceptionMessage(ex));
        }
        finally
        {
            IsDatabaseMaintenanceRunning = false;
            IsBusy = false;
            NotifyDatabaseMaintenanceCommands();
        }
        await RefreshDatabaseStatusAsync().ConfigureAwait(true);
    }

    private void NotifyDatabaseMaintenanceCommands()
    {
        RefreshDatabaseStatusCommand.NotifyCanExecuteChanged();
        OptimizeDatabaseCommand.NotifyCanExecuteChanged();
        AnalyzeDatabaseCommand.NotifyCanExecuteChanged();
        FullIntegrityCheckCommand.NotifyCanExecuteChanged();
        RebuildWorkOrderSearchCommand.NotifyCanExecuteChanged();
        RebuildActivityHistorySearchCommand.NotifyCanExecuteChanged();
        RebuildReviewDatabaseCommand.NotifyCanExecuteChanged();
    }

    private void TryAutoPrintWorkOrder(WorkOrder workOrder)
    {
        try
        {
            var printSettings = _userSettingsService.LoadPrintSettings();
            if (!printSettings.AutoPrintWorkOrderActions) return;
            var asset = workOrder.AssetId.HasValue
                ? _assets.FirstOrDefault(item => item.AssetId == workOrder.AssetId.Value)
                : null;
            _printService.PrintWorkOrder(workOrder, asset, useDefaultPrinter: true);
        }
        catch (Exception ex)
        {
            _diagnosticLogger.LogError("AutoPrint", ex, "Automatic Work Order printing failed.");
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes.ToString(CultureInfo.InvariantCulture)} B";
        var kib = bytes / 1024d;
        if (kib < 1024) return $"{kib.ToString("N1", CultureInfo.CurrentCulture)} KB";
        var mib = kib / 1024d;
        if (mib < 1024) return $"{mib.ToString("N1", CultureInfo.CurrentCulture)} MB";
        return $"{(mib / 1024d).ToString("N2", CultureInfo.CurrentCulture)} GB";
    }
}
