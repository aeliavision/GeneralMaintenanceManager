using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GeneralMaintenanceManager.App.Collections;
using GeneralMaintenanceManager.App.Models;
using GeneralMaintenanceManager.App.Services;
using GeneralMaintenanceManager.Core.Abstractions;
using GeneralMaintenanceManager.Core.Entities;
using GeneralMaintenanceManager.Core.Enums;
using GeneralMaintenanceManager.Core.Models;
using GeneralMaintenanceManager.Infrastructure.Data;

namespace GeneralMaintenanceManager.App.ViewModels;

public sealed partial class MainViewModel
{
    private void AddCustomMaintenanceType()
    {
        var normalized = CustomMaintenanceTypeStorage.Normalize(NewCustomMaintenanceType);
        if (normalized.Length == 0)
        {
            MaintenanceTypeSettingsMessage = _localization.GetString("MaintenanceTypeInvalid");
            return;
        }

        var duplicate = CustomMaintenanceTypes.Any(item => string.Equals(item, normalized, StringComparison.OrdinalIgnoreCase))
            || GetBuiltInMaintenanceTypeNames().Any(item => string.Equals(item, normalized, StringComparison.OrdinalIgnoreCase));
        if (duplicate)
        {
            MaintenanceTypeSettingsMessage = _localization.GetString("MaintenanceTypeDuplicate");
            return;
        }

        var proposedTypes = CustomMaintenanceTypes.Append(normalized).ToArray();
        try
        {
            _userSettingsService.SaveCustomMaintenanceTypes(proposedTypes);
        }
        catch (Exception ex)
        {
            _dialogService.ShowError(_localization.GetString("SettingsSaveFailedTitle"), GetExceptionMessage(ex));
            return;
        }
        CustomMaintenanceTypes.Add(normalized);
        NewCustomMaintenanceType = string.Empty;
        SelectedCustomMaintenanceType = normalized;
        MaintenanceTypeSettingsMessage = _localization.Format("MaintenanceTypeAddedFormat", normalized);
    }
    private void RemoveSelectedCustomMaintenanceType()
    {
        if (SelectedCustomMaintenanceType is not { } selected)
        {
            return;
        }

        var proposedTypes = CustomMaintenanceTypes.Where(item => !string.Equals(item, selected, StringComparison.OrdinalIgnoreCase)).ToArray();
        try
        {
            _userSettingsService.SaveCustomMaintenanceTypes(proposedTypes);
        }
        catch (Exception ex)
        {
            _dialogService.ShowError(_localization.GetString("SettingsSaveFailedTitle"), GetExceptionMessage(ex));
            return;
        }
        CustomMaintenanceTypes.Remove(selected);
        SelectedCustomMaintenanceType = null;
        MaintenanceTypeSettingsMessage = _localization.Format("MaintenanceTypeRemovedFormat", selected);
    }
    private async Task CreateBackupAsync()
    {
        var destinationFilePath = _dialogService.PickBackupSavePath(
            _backupService.BackupsDirectory,
            _backupService.GetSuggestedBackupFileName());
        if (destinationFilePath is null)
        {
            return;
        }

        IsBusy = true;
        IsBackupOperationRunning = true;
        BackupProgressPercent = 0;
        BackupProgressText = _localization.GetString("BackupProgressPreparing");
        StatusText = _localization.GetString("StatusCreatingBackup");

        try
        {
            var progress = new Progress<BackupProgress>(UpdateBackupProgress);

            // Backup includes SQLite snapshots, validation and ZIP compression. Run the
            // operation away from the WPF dispatcher so the Settings view remains responsive.
            var result = await Task.Run(() => _backupService.CreateBackupAsync(destinationFilePath, progress))
                .ConfigureAwait(true);

            BackupProgressPercent = 100;
            BackupProgressText = _localization.GetString("BackupProgressBackupComplete");
            StatusText = _localization.GetString("StatusBackupCreated");
            var sizeMb = result.SizeBytes / (1024d * 1024d);
            _dialogService.ShowInformation(
                _localization.GetString("BackupCreatedTitle"),
                _localization.Format("BackupCreatedMessageFormat", result.FilePath, sizeMb));
        }
        catch (Exception ex)
        {
            _diagnosticLogger.LogError("CreateBackup", ex, "Backup creation failed.");
            StatusText = _localization.GetString("StatusBackupFailed");
            _dialogService.ShowError(
                _localization.GetString("BackupFailedTitle"),
                GetExceptionMessage(ex));
        }
        finally
        {
            IsBackupOperationRunning = false;
            IsBusy = false;
        }
    }
    private async Task RestoreBackupAsync()
    {
        var filePath = _dialogService.PickBackupFile();
        if (filePath is null)
        {
            return;
        }

        if (!_dialogService.Confirm(
                _localization.GetString("RestoreBackupConfirmTitle"),
                _localization.GetString("RestoreBackupConfirmMessage")))
        {
            return;
        }

        IsBusy = true;
        IsBackupOperationRunning = true;
        BackupProgressPercent = 0;
        BackupProgressText = _localization.GetString("RestoreProgressPreparing");
        StatusText = _localization.GetString("StatusRestoringBackup");

        try
        {
            var progress = new Progress<BackupProgress>(UpdateBackupProgress);

            // Stop report/history readers before the live Data directory is replaced.
            // The shared gate ensures a query that was already in flight has exited
            // before BackupService starts moving database files.
            _historyCancellation?.Cancel();
            _globalHistoryCancellation?.Cancel();
            await _historyReadGate.WaitAsync().ConfigureAwait(true);
            RestoreResult result;
            try
            {
                using var databaseLease = await _databaseActivityGate.EnterExclusiveAsync().ConfigureAwait(true);
                // Restore performs ZIP validation, a pre-restore safety backup, extraction,
                // database verification and replacement. Keep all of that off the UI thread.
                result = await Task.Run(() => _backupService.RestoreBackupAsync(filePath, progress))
                    .ConfigureAwait(true);
            }
            finally
            {
                _historyReadGate.Release();
            }

            BackupProgressPercent = 100;
            BackupProgressText = _localization.GetString("RestoreProgressComplete");

            var restoredLanguage = _userSettingsService.LoadLanguageCode();
            _localization.SetLanguage(restoredLanguage);
            _selectedLanguageCode = _localization.CurrentLanguageCode;
            OnPropertyChanged(nameof(SelectedLanguageCode));

            _isWorkOrdersEnabled = _userSettingsService.LoadWorkOrdersEnabled();
            _isPreventiveMaintenanceEnabled = _userSettingsService.LoadPreventiveMaintenanceEnabled();
            _isAssetTrackingEnabled = _userSettingsService.LoadAssetTrackingEnabled();
            CustomMaintenanceTypes.Clear();
            foreach (var maintenanceType in _userSettingsService.LoadCustomMaintenanceTypes()) CustomMaintenanceTypes.Add(maintenanceType);
            OnPropertyChanged(nameof(IsWorkOrdersEnabled));
            OnPropertyChanged(nameof(IsPreventiveMaintenanceEnabled));
            OnPropertyChanged(nameof(IsAssetTrackingEnabled));
            OnPropertyChanged(nameof(HasOptionalFeaturesEnabled));
            GeneratePlanWorkOrderCommand.NotifyCanExecuteChanged();

            InvalidateEntrySuggestionCaches();
            if (IsAssetTrackingEnabled)
            {
                await ReloadAssetAsync(SelectedAsset?.AssetId).ConfigureAwait(true);
                await ReloadReviewAsync().ConfigureAwait(true);
            }
            else
            {
                _assets.ReplaceAll([]);
                _reviewItems.ReplaceAll([]);
                SelectedAsset = null;
                SelectedReviewItem = null;
            }
            await ReloadYearsAsync().ConfigureAwait(true);
            await ReloadOperationsAsync().ConfigureAwait(true);
            if (IsAssetTrackingEnabled) await LoadHistorySafelyAsync().ConfigureAwait(true);

            StatusText = _localization.GetString("StatusRestoreComplete");
            _dialogService.ShowInformation(
                _localization.GetString("RestoreCompleteTitle"),
                _localization.Format("RestoreCompleteMessageFormat", result.SafetyBackupPath));
        }
        catch (Exception ex)
        {
            _diagnosticLogger.LogError("RestoreBackup", ex, "Backup restore failed.");
            StatusText = _localization.GetString("StatusRestoreFailed");
            _dialogService.ShowError(
                _localization.GetString("RestoreFailedTitle"),
                GetExceptionMessage(ex));
        }
        finally
        {
            IsBackupOperationRunning = false;
            IsBusy = false;
        }
    }
    private void UpdateBackupProgress(BackupProgress progress)
    {
        BackupProgressPercent = progress.Percent;
        BackupProgressText = progress.Stage switch
        {
            BackupProgressStage.Preparing => _localization.GetString("BackupProgressPreparing"),
            BackupProgressStage.ValidatingCurrentData => _localization.GetString("BackupProgressValidatingCurrentData"),
            BackupProgressStage.SnapshottingData => progress.TotalItems > 0
                ? _localization.Format(
                    "BackupProgressSnapshottingFormat",
                    Math.Min(progress.ProcessedItems, progress.TotalItems),
                    progress.TotalItems)
                : _localization.GetString("BackupProgressSnapshotting"),
            BackupProgressStage.ValidatingSnapshot => _localization.GetString("BackupProgressValidatingSnapshot"),
            BackupProgressStage.CompressingBackup => progress.TotalItems > 0
                ? _localization.Format(
                    "BackupProgressCompressingFormat",
                    Math.Min(progress.ProcessedItems, progress.TotalItems),
                    progress.TotalItems)
                : _localization.GetString("BackupProgressCompressing"),
            BackupProgressStage.FinalizingBackup => _localization.GetString("BackupProgressFinalizing"),
            BackupProgressStage.BackupCompleted => _localization.GetString("BackupProgressBackupComplete"),
            BackupProgressStage.CreatingSafetyBackup => _localization.GetString("RestoreProgressSafetyBackup"),
            BackupProgressStage.OpeningBackup => _localization.GetString("RestoreProgressOpeningBackup"),
            BackupProgressStage.ExtractingBackup => progress.TotalItems > 0
                ? _localization.Format(
                    "RestoreProgressExtractingFormat",
                    Math.Min(progress.ProcessedItems, progress.TotalItems),
                    progress.TotalItems)
                : _localization.GetString("RestoreProgressExtracting"),
            BackupProgressStage.ValidatingBackup => _localization.GetString("RestoreProgressValidatingBackup"),
            BackupProgressStage.ReplacingData => _localization.GetString("RestoreProgressReplacingData"),
            BackupProgressStage.InitializingDatabase => _localization.GetString("RestoreProgressInitializingDatabase"),
            BackupProgressStage.VerifyingRestoredData => _localization.GetString("RestoreProgressVerifyingData"),
            BackupProgressStage.RestoreCompleted => _localization.GetString("RestoreProgressComplete"),
            _ => _localization.GetString("StatusCreatingBackup")
        };

        StatusText = BackupProgressText;
    }
    private void OpenBackupFolder()
    {
        try
        {
            Directory.CreateDirectory(_backupService.BackupsDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = _backupService.BackupsDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _dialogService.ShowError(
                _localization.GetString("ErrorOpenBackupFolderFailedTitle"),
                GetExceptionMessage(ex));
        }
    }
}
