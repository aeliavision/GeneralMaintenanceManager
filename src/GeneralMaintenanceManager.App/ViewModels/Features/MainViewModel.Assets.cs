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
    private async Task ReloadAssetAsync(Guid? preferredAssetId)
    {
        // Microsoft.Data.Sqlite ultimately performs synchronous file I/O. Keep the
        // database read off the WPF dispatcher so a refresh never stalls window input.
        var items = await Task.Run(() => _assetService.GetAllAsync()).ConfigureAwait(true);

        // Replacing hundreds of rows one-by-one causes repeated DataGrid layout passes.
        // Publish one Reset notification so the virtualized grid refreshes as a batch.
        _assets.ReplaceAll(items);

        if (AssetView is null)
        {
            AssetView = CollectionViewSource.GetDefaultView(_assets);
            AssetView.Filter = MatchesSearch;
        }

        OnPropertyChanged(nameof(AssetCount));
        OnPropertyChanged(nameof(AssetCountLabel));
        OnPropertyChanged(nameof(InServiceAssetCount));
        OnPropertyChanged(nameof(UnderMaintenanceAssetCount));
        OnPropertyChanged(nameof(OutOfServiceAssetCount));

        var previousSuppression = _suppressHistoryReload;
        _suppressHistoryReload = true;
        try
        {
            SelectedAsset = preferredAssetId is null
                ? _assets.FirstOrDefault()
                : _assets.FirstOrDefault(item => item.AssetId == preferredAssetId.Value)
                    ?? _assets.FirstOrDefault();
        }
        finally
        {
            _suppressHistoryReload = previousSuppression;
        }
    }
    private async Task ReloadReviewAsync(bool refreshDetection = true)
    {
        if (refreshDetection)
        {
            await _reviewService.RefreshAsync().ConfigureAwait(true);
        }
        var cases = await _reviewService.GetPendingAsync().ConfigureAwait(true);
        PublishReviewCases(cases.Where(item => !_reviewDeferredForVisit.Contains(item.ReviewId)));
    }
    private void PublishReviewCases(IEnumerable<ReviewCase> cases)
    {
        var selectedId = SelectedReviewItem?.ReviewId;
        _reviewItems.ReplaceAll(cases.Select(item => new ReviewCaseViewModel(item, _localization)));
        SelectedReviewItem = selectedId.HasValue
            ? ReviewItems.FirstOrDefault(item => item.ReviewId == selectedId.Value) ?? ReviewItems.FirstOrDefault()
            : ReviewItems.FirstOrDefault();
        OnPropertyChanged(nameof(ReviewCount));
    }
    private void RebuildReviewPresentation()
    {
        // Language changes only affect labels/reasons. Reuse the in-memory cases instead
        // of touching SQLite or recomputing duplicate detection.
        var cases = ReviewItems.Select(item => item.Source).ToArray();
        PublishReviewCases(cases);
    }
    private async Task EditReviewAAsync(ReviewCaseViewModel? item) => await EditReviewAssetAsync(item?.AssetA).ConfigureAwait(true);
    private async Task EditReviewBAsync(ReviewCaseViewModel? item) => await EditReviewAssetAsync(item?.AssetB).ConfigureAwait(true);
    private async Task EditReviewAssetAsync(Asset? asset)
    {
        if (asset is null)
        {
            return;
        }
        var dialogResult = _dialogService.ShowAssetDialog(asset);
        if (dialogResult is null)
        {
            return;
        }

        if (dialogResult.DeleteRequested)
        {
            await ArchiveAssetConfirmedAsync(asset).ConfigureAwait(true);
            return;
        }

        var draft = dialogResult.Draft;
        if (draft is null)
        {
            return;
        }

        try
        {
            IsBusy = true;
            var auditReason = _localization.GetString("ReviewCorrectionAuditReason");
            await _assetService.UpdateAsync(asset.AssetId, draft, auditReason).ConfigureAwait(true);
            await ReloadAssetAsync(asset.AssetId).ConfigureAwait(true);
            await ReloadReviewAsync().ConfigureAwait(true);
            await LoadHistorySafelyAsync().ConfigureAwait(true);
            StatusText = _localization.GetString("ReviewDataUpdated");
        }
        catch (Exception ex)
        {
            _dialogService.ShowError(_localization.GetString("ReviewActionFailed"), GetExceptionMessage(ex));
        }
        finally { IsBusy = false; }
    }
    private async Task AssignAssetAAsync(ReviewCaseViewModel? item) => await AssignAssetAsync(item?.AssetA).ConfigureAwait(true);
    private async Task AssignAssetBAsync(ReviewCaseViewModel? item) => await AssignAssetAsync(item?.AssetB).ConfigureAwait(true);
    private async Task AssignAssetAsync(Asset? asset)
    {
        if (asset is null)
        {
            return;
        }
        var draft = _dialogService.ShowAssetNumberDialog(asset);
        if (draft is null)
        {
            return;
        }
        try
        {
            IsBusy = true;
            await _assetService.AssignAssetNumberAsync(asset.AssetId, draft.AssetNumber, draft.Reason).ConfigureAwait(true);
            await ReloadAssetAsync(asset.AssetId).ConfigureAwait(true);
            await ReloadReviewAsync().ConfigureAwait(true);
            await LoadHistorySafelyAsync().ConfigureAwait(true);
            StatusText = _localization.GetString("ReviewAssetAssigned");
        }
        catch (Exception ex)
        {
            _dialogService.ShowError(_localization.GetString("ReviewActionFailed"), GetExceptionMessage(ex));
        }
        finally { IsBusy = false; }
    }
    private async Task KeepBothAsync(ReviewCaseViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        try
        {
            IsBusy = true;
            await _reviewService.KeepBothAsync(item.ReviewId).ConfigureAwait(true);
            await ReloadReviewAsync(refreshDetection: false).ConfigureAwait(true);
            StatusText = _localization.GetString("ReviewKeepBothSaved");
        }
        catch (Exception ex)
        {
            _dialogService.ShowError(_localization.GetString("ReviewActionFailed"), GetExceptionMessage(ex));
        }
        finally
        {
            IsBusy = false;
        }
    }
    private async Task ReviewLaterAsync(ReviewCaseViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        // "Review later" deliberately does not resolve the database record. It stays
        // Pending so it can return on the next visit. Hide it from the current Review
        // visit immediately so the action has clear, useful feedback.
        await _reviewService.ResolveLaterAsync(item.ReviewId).ConfigureAwait(true);
        _reviewDeferredForVisit.Add(item.ReviewId);

        var removedIndex = ReviewItems.IndexOf(item);
        var wasSelected = SelectedReviewItem?.ReviewId == item.ReviewId;
        _reviewItems.Remove(item);

        if (wasSelected)
        {
            SelectedReviewItem = ReviewItems.Count == 0
                ? null
                : ReviewItems[Math.Min(Math.Max(removedIndex, 0), ReviewItems.Count - 1)];
        }

        OnPropertyChanged(nameof(ReviewCount));
        StatusText = _localization.GetString("ReviewLaterSaved");
    }
    private async Task MergeIntoAAsync(ReviewCaseViewModel? item) => await MergeReviewAsync(item, item?.AssetA).ConfigureAwait(true);
    private async Task MergeIntoBAsync(ReviewCaseViewModel? item) => await MergeReviewAsync(item, item?.AssetB).ConfigureAwait(true);
    private async Task MergeReviewAsync(ReviewCaseViewModel? review, Asset? survivor)
    {
        if (review is null || survivor is null || review.AssetB is null)
        {
            return;
        }
        if (!_dialogService.Confirm(_localization.GetString("ReviewMergeTitle"), _localization.Format("ReviewMergeConfirmFormat", survivor.AssetName)))
        {
            return;
        }
        try
        {
            IsBusy = true;
            await _reviewService.MergeAsync(review.ReviewId, survivor.AssetId).ConfigureAwait(true);
            await ReloadAssetAsync(survivor.AssetId).ConfigureAwait(true);
            await ReloadReviewAsync().ConfigureAwait(true);
            await LoadHistorySafelyAsync().ConfigureAwait(true);
            StatusText = _localization.GetString("ReviewMergeComplete");
        }
        catch (Exception ex)
        {
            _dialogService.ShowError(_localization.GetString("ReviewActionFailed"), GetExceptionMessage(ex));
        }
        finally { IsBusy = false; }
    }
    private async Task AddAssetAsync()
    {
        try
        {
            var dialogResult = _dialogService.ShowAssetDialog(null);
            if (dialogResult is null || dialogResult.DeleteRequested || dialogResult.Draft is not { } draft)
            {
                return;
            }

            IsBusy = true;
            var addedAsset = await _assetService.AddAsync(draft).ConfigureAwait(true);
            await ReloadAssetAsync(addedAsset.AssetId).ConfigureAwait(true);
            await ReloadReviewAsync().ConfigureAwait(true);
            await LoadHistorySafelyAsync().ConfigureAwait(true);
            StatusText = _localization.Format("StatusAssetAddedFormat", GetAssetDisplay(addedAsset));
        }
        catch (Exception ex)
        {
            _dialogService.ShowError(
                _localization.GetString("ErrorAddAssetFailedTitle"),
                GetExceptionMessage(ex));
        }
        finally
        {
            IsBusy = false;
        }
    }
    private async Task EditAssetAsync()
    {
        var asset = SelectedAsset;
        if (asset is null)
        {
            return;
        }

        try
        {
            var dialogResult = _dialogService.ShowAssetDialog(asset);
            if (dialogResult is null)
            {
                return;
            }

            if (dialogResult.DeleteRequested)
            {
                await ArchiveAssetConfirmedAsync(asset).ConfigureAwait(true);
                return;
            }

            var draft = dialogResult.Draft;
            if (draft is null)
            {
                return;
            }

            IsBusy = true;
            await _assetService.UpdateAsync(asset.AssetId, draft).ConfigureAwait(true);
            await ReloadAssetAsync(asset.AssetId).ConfigureAwait(true);
            await ReloadReviewAsync().ConfigureAwait(true);
            await LoadHistorySafelyAsync().ConfigureAwait(true);
            StatusText = _localization.Format("StatusAssetUpdatedFormat", GetAssetDisplay(asset));
        }
        catch (Exception ex)
        {
            _dialogService.ShowError(
                _localization.GetString("ErrorUpdateAssetFailedTitle"),
                GetExceptionMessage(ex));
        }
        finally
        {
            IsBusy = false;
        }
    }
    private async Task ArchiveAssetConfirmedAsync(Asset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);

        var display = GetAssetDisplay(asset);
        try
        {
            IsBusy = true;
            StatusText = _localization.GetString("StatusRemovingAsset");
            await _assetService.ArchiveAsync(
                    asset.AssetId,
                    "Asset record removed by the user after typed DELETE confirmation.")
                .ConfigureAwait(true);

            await ReloadAssetAsync(preferredAssetId: null).ConfigureAwait(true);
            await ReloadReviewAsync(refreshDetection: false).ConfigureAwait(true);
            await LoadHistorySafelyAsync().ConfigureAwait(true);
            StatusText = _localization.Format("StatusAssetRemovedFormat", display);
        }
        catch (Exception ex)
        {
            _dialogService.ShowError(
                _localization.GetString("ErrorRemoveAssetFailedTitle"),
                GetExceptionMessage(ex));
        }
        finally
        {
            IsBusy = false;
        }
    }
    private async Task ExportAssetExcelAsync()
    {
        var suggestedFileName = $"MaintenanceManager-Assets-{DateTime.Today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)}.xlsx";
        var filePath = _dialogService.PickAssetExportExcelPath(suggestedFileName);
        if (filePath is null)
        {
            return;
        }

        await ExportAssetAsync(filePath, exportExcel: true).ConfigureAwait(true);
    }
    private async Task ExportAssetCsvAsync()
    {
        var suggestedFileName = $"MaintenanceManager-Assets-{DateTime.Today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)}.csv";
        var filePath = _dialogService.PickAssetExportCsvPath(suggestedFileName);
        if (filePath is null)
        {
            return;
        }

        await ExportAssetAsync(filePath, exportExcel: false).ConfigureAwait(true);
    }
    private async Task ExportAssetAsync(string filePath, bool exportExcel)
    {
        try
        {
            IsBusy = true;
            StatusText = _localization.GetString("StatusExportingAsset");

            var exported = exportExcel
                ? await Task.Run(() => _assetExportService.ExportExcelAsync(filePath)).ConfigureAwait(true)
                : await Task.Run(() => _assetExportService.ExportCsvAsync(filePath)).ConfigureAwait(true);

            StatusText = _localization.Format("StatusAssetExportCompleteFormat", exported);
            _dialogService.ShowInformation(
                _localization.GetString("ExportAssetCompleteTitle"),
                _localization.Format("ExportAssetCompleteMessageFormat", exported, filePath));
        }
        catch (Exception ex)
        {
            StatusText = _localization.GetString("StatusAssetExportFailed");
            _dialogService.ShowError(
                _localization.GetString("ErrorExportAssetFailedTitle"),
                GetExceptionMessage(ex));
        }
        finally
        {
            IsBusy = false;
        }
    }
    private async Task ImportExcelAsync()
    {
        var filePath = _dialogService.PickExcelFile();
        if (filePath is null)
        {
            return;
        }

        IsBusy = true;
        IsImporting = true;
        ImportProgressPercent = 0;
        ImportProgressText = _localization.GetString("ImportProgressPreparing");
        StatusText = _localization.GetString("StatusImportingExcel");

        try
        {
            var progress = new Progress<ExcelImportProgress>(UpdateImportProgress);

            // ClosedXML workbook parsing is synchronous and CPU/file-system heavy. Run the
            // entire import operation away from the WPF dispatcher so the window remains
            // responsive while real progress is marshalled back through Progress<T>.
            var result = await Task.Run(() => _excelImportService.ImportAsync(filePath, progress))
                .ConfigureAwait(true);

            ImportProgressPercent = 100;
            ImportProgressText = _localization.GetString("ImportProgressComplete");
            StatusText = ImportProgressText;

            await ReloadAssetAsync(SelectedAsset?.AssetId).ConfigureAwait(true);
            await ReloadYearsAsync().ConfigureAwait(true);
            await ReloadReviewAsync(refreshDetection: false).ConfigureAwait(true);
            await LoadHistorySafelyAsync().ConfigureAwait(true);

            var lines = new[]
            {
                _localization.Format("ImportAddedFormat", result.Added),
                _localization.Format("ImportUpdatedFormat", result.Updated),
                _localization.Format("ImportUnchangedFormat", result.Unchanged),
                _localization.Format("ImportMissingAssetFormat", result.MissingAssetNumber),
                _localization.Format("ImportDuplicateAssetFormat", result.DuplicateAssetNumber),
                _localization.Format("ImportInvalidRowsFormat", result.InvalidRows)
            };

            var message = string.Join(Environment.NewLine, lines);
            message += result.LogFilePath is null
                ? $"{Environment.NewLine}{Environment.NewLine}{_localization.GetString("ImportNoIssues")}"
                : $"{Environment.NewLine}{Environment.NewLine}{_localization.Format("ImportReviewLogFormat", result.LogFilePath)}";

            _dialogService.ShowInformation(
                _localization.GetString("ImportCompleteTitle"),
                message);

            StatusText = _localization.Format("StatusImportCompleteFormat", result.Processed);
        }
        catch (Exception ex)
        {
            StatusText = _localization.GetString("StatusExcelImportFailed");
            _dialogService.ShowError(
                _localization.GetString("ErrorImportFailedTitle"),
                GetExceptionMessage(ex));
        }
        finally
        {
            IsImporting = false;
            IsBusy = false;
        }
    }
    private void UpdateImportProgress(ExcelImportProgress progress)
    {
        ImportProgressPercent = progress.Percent;
        ImportProgressText = progress.Stage switch
        {
            ExcelImportStage.OpeningWorkbook => _localization.GetString("ImportProgressOpeningWorkbook"),
            ExcelImportStage.ReadingRows => progress.TotalRows > 0
                ? _localization.Format(
                    "ImportProgressReadingRowsFormat",
                    Math.Min(progress.ProcessedRows, progress.TotalRows),
                    progress.TotalRows)
                : _localization.GetString("ImportProgressReadingRows"),
            ExcelImportStage.LoadingExistingRecords => _localization.GetString("ImportProgressLoadingExisting"),
            ExcelImportStage.UpdatingDatabase => progress.TotalRows > 0
                ? _localization.Format(
                    "ImportProgressUpdatingDatabaseFormat",
                    Math.Min(progress.ProcessedRows, progress.TotalRows),
                    progress.TotalRows)
                : _localization.GetString("ImportProgressUpdatingDatabase"),
            ExcelImportStage.RecordingHistory => _localization.GetString("ImportProgressRecordingHistory"),
            ExcelImportStage.WritingReviewLog => _localization.GetString("ImportProgressWritingLog"),
            ExcelImportStage.Completed => _localization.GetString("ImportProgressComplete"),
            _ => _localization.GetString("StatusImportingExcel")
        };

        StatusText = ImportProgressText;
    }
    private void OpenDataFolder()
    {
        try
        {
            Directory.CreateDirectory(_dataLocationService.DataDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = _dataLocationService.DataDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _dialogService.ShowError(
                _localization.GetString("ErrorOpenDataFolderFailedTitle"),
                GetExceptionMessage(ex));
        }
    }
}
