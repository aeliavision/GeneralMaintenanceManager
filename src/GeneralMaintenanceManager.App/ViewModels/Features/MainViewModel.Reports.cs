using GeneralMaintenanceManager.App.Models;
using GeneralMaintenanceManager.App.Services;
using GeneralMaintenanceManager.Core.Entities;
using GeneralMaintenanceManager.Core.Models;

namespace GeneralMaintenanceManager.App.ViewModels;

public sealed partial class MainViewModel
{
    private void RefreshGlobalHistoryFilters() => _ = LoadGlobalHistorySafelyAsync();

    private void SetHistoryMode(string? mode)
    {
        if (mode is not (GlobalHistoryModes.Maintenance or GlobalHistoryModes.WorkOrders or GlobalHistoryModes.PreventiveMaintenance or GlobalHistoryModes.AllActivity)) return;
        if (string.Equals(SelectedHistoryMode, mode, StringComparison.Ordinal)) return;

        if (_historyMaintenanceTypeFilter.Length > 0)
        {
            _historyMaintenanceTypeFilter = string.Empty;
            OnPropertyChanged(nameof(HistoryMaintenanceTypeFilter));
        }
        if (_historyActivityTypeFilter.Length > 0)
        {
            _historyActivityTypeFilter = string.Empty;
            OnPropertyChanged(nameof(HistoryActivityTypeFilter));
        }

        SelectedHistoryMode = mode;
        if (IsReportsView) _ = LoadGlobalHistorySafelyAsync();
    }

    private async Task LoadMoreGlobalHistoryAsync()
    {
        if (!HasMoreGlobalHistory || _globalHistoryCursor is null) return;
        await LoadGlobalHistorySafelyAsync(append: true).ConfigureAwait(true);
    }

    private async Task LoadGlobalHistorySafelyAsync(bool append = false)
    {
        var currentCancellation = new CancellationTokenSource();
        var previousCancellation = _globalHistoryCancellation;
        _globalHistoryCancellation = currentCancellation;
        previousCancellation?.Cancel();

        var cancellationToken = currentCancellation.Token;
        var gateAcquired = false;

        try
        {
            await _historyReadGate.WaitAsync(cancellationToken).ConfigureAwait(true);
            gateAcquired = true;
            using var databaseLease = await _databaseActivityGate.EnterReadAsync(cancellationToken).ConfigureAwait(true);

            if (!append)
            {
                _globalHistoryCursor = null;
                HasMoreGlobalHistory = false;
                _globalHistory.ReplaceAll([]);
                SelectedGlobalHistoryItem = null;
                RebuildMaintenanceLog();
                NotifyGlobalHistoryPresentationChanged();
            }

            var query = BuildGlobalHistoryQuery(append ? _globalHistoryCursor : null);
            // All report modes use SQLite. Keep the bounded page read off the WPF dispatcher
            // so cold-cache disk I/O cannot freeze tab switching or filter changes.
            var page = await Task.Run(
                () => _globalHistoryQueryService.GetPageAsync(query, cancellationToken),
                cancellationToken).ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();

            var viewModels = page.Items.Select(item => item.MaintenanceRecord is not null
                ? new GlobalHistoryItemViewModel(item.MaintenanceRecord, _localization)
                : new GlobalHistoryItemViewModel(item.Activity!, _localization)).ToArray();

            if (append) _globalHistory.AppendAll(viewModels);
            else _globalHistory.ReplaceAll(viewModels);

            _globalHistoryCursor = page.NextCursor;
            HasMoreGlobalHistory = page.HasMore && page.NextCursor is not null;
            SelectedGlobalHistoryItem ??= _globalHistory.FirstOrDefault();
            GlobalHistoryView?.Refresh();
            RebuildMaintenanceLog();
            RebuildOperationalReport();
            OnPropertyChanged(nameof(HistorySiteSuggestions));
            OnPropertyChanged(nameof(HistoryLocationSuggestions));
            OnPropertyChanged(nameof(HistoryMaintenanceTypeSuggestions));
            OnPropertyChanged(nameof(HistoryActivityTypeSuggestions));
            OnPropertyChanged(nameof(HistoryPerformedBySuggestions));
            NotifyGlobalHistoryPresentationChanged();
            StatusText = _localization.Format("StatusGlobalHistoryLoadedFormat", GlobalHistoryCount);
        }
        catch (OperationCanceledException)
        {
            // A newer filter/year/mode request superseded this page.
        }
        catch (Exception ex)
        {
            _diagnosticLogger.LogError("GlobalHistoryLoad", ex, "Bounded global history query failed.");
            StatusText = _localization.GetString("StatusCouldNotLoadHistory");
            _dialogService.ShowError(_localization.GetString("ErrorHistoryFailedTitle"), GetExceptionMessage(ex));
        }
        finally
        {
            if (gateAcquired) _historyReadGate.Release();
            if (ReferenceEquals(_globalHistoryCancellation, currentCancellation)) _globalHistoryCancellation = null;
            currentCancellation.Dispose();
        }
    }

    private GlobalHistoryQuery BuildGlobalHistoryQuery(GlobalHistoryPageCursor? cursor)
    {
        var maintenanceWorkspace = IsMaintenanceView;
        var mode = maintenanceWorkspace ? GlobalHistoryModes.Maintenance : SelectedHistoryMode;
        var fromDate = maintenanceWorkspace ? MaintenanceFromDate : HistoryFromDate;
        var toDate = maintenanceWorkspace ? MaintenanceToDate : HistoryToDate;
        var from = fromDate.HasValue ? ToLocalStart(fromDate.Value) : (DateTimeOffset?)null;
        var to = toDate.HasValue ? ToLocalEnd(toDate.Value) : (DateTimeOffset?)null;

        return new GlobalHistoryQuery(
            Mode: mode,
            Year: SelectedYear.Year,
            FromInclusive: from,
            ToInclusive: to,
            Site: maintenanceWorkspace ? string.Empty : HistorySiteFilter.Trim(),
            Location: maintenanceWorkspace ? MaintenanceLocationFilter.Trim() : HistoryLocationFilter.Trim(),
            MaintenanceType: ResolveMaintenanceTypeFilter(maintenanceWorkspace ? MaintenanceTypeFilter : HistoryMaintenanceTypeFilter),
            ActivityType: maintenanceWorkspace ? string.Empty : ResolveActivityTypeFilter(HistoryActivityTypeFilter),
            PerformedBy: maintenanceWorkspace ? MaintenancePerformedByFilter.Trim() : HistoryPerformedByFilter.Trim(),
            SearchText: maintenanceWorkspace ? MaintenanceSearchText.Trim() : HistorySearchText.Trim(),
            IncludeInvalid: true,
            PageSize: 100,
            Cursor: cursor);
    }

    private string ResolveMaintenanceTypeFilter(string value)
    {
        var normalized = value.Trim();
        if (normalized.Length == 0) return string.Empty;
        foreach (var candidate in MaintenanceTypeCatalog.BuiltIn.Concat(CustomMaintenanceTypes))
        {
            var display = MaintenanceTextPresentation.LocalizeMaintenanceType(candidate, _localization);
            if (string.Equals(normalized, display, StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, candidate, StringComparison.OrdinalIgnoreCase))
                return candidate;
        }
        return normalized;
    }

    private string ResolveActivityTypeFilter(string value)
    {
        var normalized = value.Trim();
        if (normalized.Length == 0) return string.Empty;
        string[] candidates =
        [
            ActivityTypes.MaintenanceCreated, ActivityTypes.MaintenanceEdited, ActivityTypes.MaintenanceMarkedInvalid,
            ActivityTypes.WorkOrderCreated, ActivityTypes.WorkOrderAssigned, ActivityTypes.WorkOrderStarted,
            ActivityTypes.WorkOrderResumed, ActivityTypes.WorkOrderPutOnHold, ActivityTypes.WorkOrderCompleted,
            ActivityTypes.WorkOrderCompletionRecovered, ActivityTypes.WorkOrderCancelled, ActivityTypes.WorkOrderEdited,
            ActivityTypes.PreventivePlanCreated, ActivityTypes.PreventivePlanEdited, ActivityTypes.PreventivePlanActivated,
            ActivityTypes.PreventivePlanPaused, ActivityTypes.PreventivePlanDue, ActivityTypes.PreventivePlanGeneratedWorkOrder,
            ActivityTypes.PreventivePlanCompletedOccurrence
        ];
        foreach (var candidate in candidates)
        {
            var display = MaintenanceTextPresentation.LocalizeActivityType(candidate, _localization);
            if (string.Equals(normalized, display, StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, candidate, StringComparison.OrdinalIgnoreCase))
                return candidate;
        }
        return normalized;
    }

    private static DateTimeOffset ToLocalStart(DateTime value) =>
        new(DateTime.SpecifyKind(value.Date, DateTimeKind.Local));

    private static DateTimeOffset ToLocalEnd(DateTime value) =>
        new(DateTime.SpecifyKind(value.Date.AddDays(1).AddTicks(-1), DateTimeKind.Local));

    private async Task OpenSelectedGlobalHistoryDetailsAsync()
    {
        var item = SelectedGlobalHistoryItem;
        if (item is null) return;

        if (item.SourceMaintenanceRecord is not null)
        {
            var record = await _maintenanceRecordService.GetByNumberAsync(item.SourceMaintenanceRecord.MaintenanceNumber).ConfigureAwait(true)
                ?? item.SourceMaintenanceRecord;
            var activities = await _maintenanceRecordService.GetActivityAsync(record.MaintenanceNumber).ConfigureAwait(true);
            var action = _dialogService.ShowMaintenanceRecordDetailsDialog(record, activities);
            if (action == MaintenanceRecordDetailsAction.Edit)
            {
                await EnsureEntrySuggestionsLoadedAsync().ConfigureAwait(true);
                var editDraft = _dialogService.ShowMaintenanceRecordEditDialog(BuildMaintenanceEntryOptions(), record);
                if (editDraft is not null)
                {
                    await _maintenanceRecordService.UpdateAsync(record.MaintenanceRecordId, record.ReferenceYear, editDraft).ConfigureAwait(true);
                    InvalidateEntrySuggestionCaches();
                    await ReloadOperationsAsync().ConfigureAwait(true);
                    await LoadGlobalHistorySafelyAsync().ConfigureAwait(true);
                    StatusText = _localization.Format("StatusMaintenanceRecordUpdatedFormat", record.MaintenanceNumber);
                }
            }
            else if (action == MaintenanceRecordDetailsAction.MarkInvalid)
            {
                var reason = _dialogService.ShowRequiredTextPrompt(
                    _localization.GetString("MarkMaintenanceInvalid"),
                    _localization.GetString("VoidReason"),
                    _localization.GetString("ValidationInvalidReasonRequired"));
                if (reason is not null)
                {
                    await _maintenanceRecordService.MarkInvalidAsync(record.MaintenanceRecordId, record.ReferenceYear, reason).ConfigureAwait(true);
                    await ReloadOperationsAsync().ConfigureAwait(true);
                    await LoadGlobalHistorySafelyAsync().ConfigureAwait(true);
                    StatusText = _localization.Format("StatusMaintenanceRecordMarkedInvalidFormat", record.MaintenanceNumber);
                }
            }
            else if (action == MaintenanceRecordDetailsAction.Print)
            {
                await PrintMaintenanceRecordAsync(record, activities).ConfigureAwait(true);
            }
            return;
        }

        if (item.SourceWorkOrder is not null)
        {
            await OpenWorkOrderDetailsAsync(item.SourceWorkOrder).ConfigureAwait(true);
            return;
        }

        if (item.SourceMaintenancePlan is not null || item.SourceActivity is not null)
        {
            _dialogService.ShowReportItemDetailsDialog(item);
            return;
        }

        _dialogService.ShowReportItemDetailsDialog(item);
    }
    private async Task ReprintMaintenanceByNumberAsync()
    {
        var input = MaintenanceReprintNumber.Trim();
        if (input.Length == 0) return;

        var normalized = input.ToUpperInvariant();
        if (normalized.All(char.IsDigit))
        {
            var year = IsReportsView ? (SelectedYear.Year ?? DateTime.Today.Year) : DateTime.Today.Year;
            if (int.TryParse(normalized, out var sequence) && sequence > 0)
                normalized = $"MNT-{year:0000}-{sequence:0000000}";
        }

        try
        {
            var record = await _maintenanceRecordService.GetByNumberAsync(normalized).ConfigureAwait(true);
            if (record is null)
            {
                StatusText = _localization.GetString("StatusMaintenanceNumberNotFound");
                _dialogService.ShowError(
                    _localization.GetString("MaintenanceNumberNotFoundTitle"),
                    _localization.Format("MaintenanceNumberNotFoundFormat", normalized));
                return;
            }

            // Reprint workflow intentionally opens the permanent record first.
            // The user can review the record and choose Print from its details dialog.
            SelectedGlobalHistoryItem = new GlobalHistoryItemViewModel(record, _localization);
            await OpenSelectedGlobalHistoryDetailsAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusText = _localization.GetString("StatusCouldNotLoadHistory");
            _dialogService.ShowError(_localization.GetString("ErrorHistoryFailedTitle"), GetExceptionMessage(ex));
        }
    }
    private void PrintGlobalHistory()
    {
        try
        {
            var visible = GlobalHistoryView?.Cast<object>().OfType<GlobalHistoryItemViewModel>().ToArray()
                ?? GlobalHistory.ToArray();
            if (visible.Length == 0) return;

            var scope = SelectedYear.DisplayName;
            if (!string.IsNullOrWhiteSpace(HistorySearchText))
                scope = $"{scope} · {HistorySearchText.Trim()}";
            if (HasMoreGlobalHistory)
                scope = $"{scope} · {_localization.GetString("LoadedRowsOnly")}";

            var printed = _printService.PrintGlobalHistory(visible, scope);
            StatusText = printed ? _localization.GetString("StatusGlobalHistoryPrintSent") : _localization.GetString("StatusPrintCancelled");
        }
        catch (Exception ex)
        {
            StatusText = _localization.GetString("StatusPrintFailed");
            _dialogService.ShowError(_localization.GetString("ErrorPrintFailedTitle"), GetExceptionMessage(ex));
        }
    }
    private async Task OpenGlobalHistoryAssetAsync()
    {
        var item = SelectedGlobalHistoryItem;
        if (item?.AssetId is not Guid assetId)
        {
            return;
        }

        var asset = _assets.FirstOrDefault(candidate => candidate.AssetId == assetId);
        if (asset is null)
        {
            return;
        }

        var previousSuppression = _suppressHistoryReload;
        _suppressHistoryReload = true;
        try
        {
            SelectedAsset = asset;
            SelectedYear = Years.FirstOrDefault(option => option.Year == item.RecordYear) ?? Years[0];
        }
        finally
        {
            _suppressHistoryReload = previousSuppression;
        }

        SetActiveView("Asset");
        await LoadHistorySafelyAsync().ConfigureAwait(true);
    }
}
