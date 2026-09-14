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
    private async Task PrintMaintenanceRecordAsync(MaintenanceRecord record, IReadOnlyList<MaintenanceActivity> activities)
    {
        try
        {
            var sourceWorkOrder = record.GeneratedByWorkOrderId.HasValue
                ? await _workOrderService.GetByIdAsync(record.GeneratedByWorkOrderId.Value).ConfigureAwait(true)
                : null;
            var printed = _printService.PrintMaintenanceRecord(record, activities, sourceWorkOrder);
            StatusText = printed ? _localization.GetString("StatusMaintenancePrintSent") : _localization.GetString("StatusPrintCancelled");
        }
        catch (Exception ex)
        {
            StatusText = _localization.GetString("StatusPrintFailed");
            _dialogService.ShowError(_localization.GetString("ErrorPrintFailedTitle"), GetExceptionMessage(ex));
        }
    }
    private async Task AddMaintenanceRecordAsync()
    {
        if (IsBusy) return;
        try
        {
            await EnsureEntrySuggestionsLoadedAsync().ConfigureAwait(true);
            var draft = _dialogService.ShowMaintenanceOrderDialog(
                BuildMaintenanceEntryOptions(),
                IsAssetTrackingEnabled ? SelectedAsset : null);
            if (draft is null) return;

            IsBusy = true;
            var order = await _workOrderService.CreateAsync(draft).ConfigureAwait(true);
            InvalidateEntrySuggestionCaches();
            SelectedWorkOrder = order;
            SetWorkOrderListMode("Open");
            SetActiveView("WorkOrders");
            await ReloadOperationsAsync().ConfigureAwait(true);
            SelectedWorkOrder = _workOrders.FirstOrDefault(item => item.WorkOrderId == order.WorkOrderId) ?? _workOrders.FirstOrDefault();
            StatusText = _localization.Format("StatusMaintenanceOrderCreatedFormat", order.WorkOrderNumber);
        }
        catch (Exception ex)
        {
            StatusText = _localization.GetString("StatusMaintenanceOrderNotCreated");
            _dialogService.ShowError(_localization.GetString("ErrorAddMaintenanceOrderFailedTitle"), GetExceptionMessage(ex));
        }
        finally
        {
            IsBusy = false;
        }
    }
    private async Task EditMaintenanceAsync(HistoryItemViewModel? historyItem)
    {
        var asset = SelectedAsset;
        if (historyItem is not { IsMaintenance: true }) return;

        try
        {
            var record = historyItem.SourceMaintenanceRecord;
            var selectedYearBeforeEdit = _selectedYear.Year;
            var eventYear = record.ReferenceYear;
            if (eventYear < DateTime.Today.Year
                && !_dialogService.Confirm(
                    _localization.GetString("ArchivedYearEditTitle"),
                    _localization.Format("ArchivedYearEditMessageFormat", eventYear)))
            {
                return;
            }

            await EnsureEntrySuggestionsLoadedAsync().ConfigureAwait(true);
            var draft = _dialogService.ShowMaintenanceRecordEditDialog(BuildMaintenanceEntryOptions(), record);
            if (draft is null) return;

            IsBusy = true;
            StatusText = _localization.Format("StatusSavingMaintenanceCorrectionFormat", record.MaintenanceNumber);
            await _maintenanceRecordService.UpdateAsync(record.MaintenanceRecordId, record.ReferenceYear, draft).ConfigureAwait(true);
            InvalidateEntrySuggestionCaches();
            if (asset is not null) await ReloadAssetAsync(asset.AssetId).ConfigureAwait(true);
            SetSelectedYearWithoutHistoryReload(selectedYearBeforeEdit is null
                ? Years[0]
                : Years.FirstOrDefault(item => item.Year == selectedYearBeforeEdit) ?? Years[0]);
            await LoadHistorySafelyAsync().ConfigureAwait(true);
            StatusText = _localization.Format("StatusMaintenanceRecordUpdatedFormat", record.MaintenanceNumber);
        }
        catch (Exception ex)
        {
            StatusText = _localization.GetString("StatusMaintenanceCorrectionFailed");
            _dialogService.ShowError(_localization.GetString("ErrorEditMaintenanceFailedTitle"), GetExceptionMessage(ex));
        }
        finally { IsBusy = false; }
    }
    private async Task ViewAuditAsync(HistoryItemViewModel? historyItem)
    {
        if (historyItem is null) return;
        try
        {
            IsBusy = true;
            var audit = await _historyService.GetAuditTrailAsync(historyItem.SourceMaintenanceRecord.MaintenanceNumber).ConfigureAwait(true);
            if (audit.Count == 0)
            {
                _dialogService.ShowInformation(_localization.GetString("AuditTrail"), _localization.GetString("AuditNoChanges"));
                return;
            }
            _dialogService.ShowAuditDialog(SelectedAsset, historyItem.SourceMaintenanceRecord, audit);
        }
        catch (Exception ex)
        {
            _dialogService.ShowError(_localization.GetString("AuditTrail"), GetExceptionMessage(ex));
        }
        finally { IsBusy = false; }
    }
}
