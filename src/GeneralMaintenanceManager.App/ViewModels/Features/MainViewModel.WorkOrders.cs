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
    private static bool IsKnownWorkOrderListMode(string? mode) => mode is "Open" or "Completed" or "Cancelled" or "All";

    private void SetWorkOrderListMode(string mode)
    {
        if (!IsKnownWorkOrderListMode(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        if (string.Equals(_workOrderListMode, mode, StringComparison.Ordinal)) return;
        _workOrderListMode = mode;
        OnPropertyChanged(nameof(WorkOrderListMode));
        OnPropertyChanged(nameof(IsWorkOrderListOpen));
        OnPropertyChanged(nameof(IsWorkOrderListCompleted));
        OnPropertyChanged(nameof(IsWorkOrderListCancelled));
        OnPropertyChanged(nameof(IsWorkOrderListAll));
    }

    private Task ShowWorkOrdersAsync() => RunOperationAsync(async () =>
    {
        SetActiveView("WorkOrders");
        SetWorkOrderListMode("Open");
        await LoadWorkOrderPageAsync(resetPaging: true).ConfigureAwait(true);
    });

    private async Task SetWorkOrderListModeAsync(string? mode)
    {
        if (!IsKnownWorkOrderListMode(mode)) return;
        SetWorkOrderListMode(mode!);
        await RunOperationAsync(() => LoadWorkOrderPageAsync(resetPaging: true)).ConfigureAwait(true);
    }

    private async Task NewWorkOrderAsync()
    {
        var draft = _dialogService.ShowWorkOrderDialog(_assets.ToArray(), preferredAsset: null);
        if (draft is null) return;
        await RunOperationAsync(async () =>
        {
            var created = await _workOrderService.CreateAsync(draft);
            SetWorkOrderListMode("Open");
            SetActiveView("WorkOrders");
            await ReloadOperationsAsync();
            StatusText = _localization.GetString("StatusWorkOrderCreated");
            TryAutoPrintWorkOrder(created);
        });
    }
    private async Task EditOrViewSelectedWorkOrderAsync()
    {
        if (SelectedWorkOrder is null) return;
        await OpenWorkOrderDetailsAsync(SelectedWorkOrder).ConfigureAwait(true);
    }
    private async Task OpenWorkOrderDetailsAsync(WorkOrder workOrder)
    {
        var selectedId = workOrder.WorkOrderId;
        var asset = workOrder.AssetId.HasValue
            ? _assets.FirstOrDefault(item => item.AssetId == workOrder.AssetId.Value)
            : null;
        var activities = await _activityHistoryService.GetForRecordAsync(ActivityRecordTypes.WorkOrder, workOrder.WorkOrderId).ConfigureAwait(true);
        MaintenanceRecord? generatedMaintenance = null;
        if (!string.IsNullOrWhiteSpace(workOrder.GeneratedMaintenanceNumber))
            generatedMaintenance = await _maintenanceRecordService.GetByNumberAsync(workOrder.GeneratedMaintenanceNumber).ConfigureAwait(true);

        var action = _dialogService.ShowWorkOrderDetailsDialog(workOrder, asset, generatedMaintenance, activities);
        if (action == WorkOrderDetailsAction.Edit && workOrder.Status is not WorkOrderStatus.Completed and not WorkOrderStatus.Cancelled)
        {
            var draft = _dialogService.ShowWorkOrderEditDialog(_assets.ToArray(), workOrder);
            if (draft is null) return;
            await RunOperationAsync(async () =>
            {
                await _workOrderService.UpdateAsync(selectedId, draft);
                await ReloadOperationsAsync();
                SelectedWorkOrder = _workOrders.FirstOrDefault(item => item.WorkOrderId == selectedId);
                StatusText = _localization.GetString("StatusWorkOrderUpdated");
            });
        }
        else if (action == WorkOrderDetailsAction.OpenMaintenanceRecord && generatedMaintenance is not null)
        {
            var maintenanceActivities = await _maintenanceRecordService.GetActivityAsync(generatedMaintenance.MaintenanceNumber).ConfigureAwait(true);
            var maintenanceAction = _dialogService.ShowMaintenanceRecordDetailsDialog(generatedMaintenance, maintenanceActivities);
            if (maintenanceAction == MaintenanceRecordDetailsAction.Edit)
            {
                await EnsureEntrySuggestionsLoadedAsync().ConfigureAwait(true);
                var editDraft = _dialogService.ShowMaintenanceRecordEditDialog(BuildMaintenanceEntryOptions(), generatedMaintenance);
                if (editDraft is not null)
                {
                    await _maintenanceRecordService.UpdateAsync(generatedMaintenance.MaintenanceRecordId, generatedMaintenance.ReferenceYear, editDraft).ConfigureAwait(true);
                    InvalidateEntrySuggestionCaches();
                    await ReloadOperationsAsync().ConfigureAwait(true);
                    StatusText = _localization.Format("StatusMaintenanceRecordUpdatedFormat", generatedMaintenance.MaintenanceNumber);
                }
            }
            else if (maintenanceAction == MaintenanceRecordDetailsAction.MarkInvalid)
            {
                var reason = _dialogService.ShowRequiredTextPrompt(
                    _localization.GetString("MarkMaintenanceInvalid"),
                    _localization.GetString("VoidReason"),
                    _localization.GetString("ValidationInvalidReasonRequired"));
                if (reason is not null)
                {
                    await _maintenanceRecordService.MarkInvalidAsync(generatedMaintenance.MaintenanceRecordId, generatedMaintenance.ReferenceYear, reason).ConfigureAwait(true);
                    await ReloadOperationsAsync().ConfigureAwait(true);
                    StatusText = _localization.Format("StatusMaintenanceRecordMarkedInvalidFormat", generatedMaintenance.MaintenanceNumber);
                }
            }
            else if (maintenanceAction == MaintenanceRecordDetailsAction.Print)
            {
                await PrintMaintenanceRecordAsync(generatedMaintenance, maintenanceActivities).ConfigureAwait(true);
            }
        }
    }
    private void PrintSelectedWorkOrder()
    {
        if (SelectedWorkOrder is null) return;
        try
        {
            var asset = SelectedWorkOrder.AssetId.HasValue
                ? _assets.FirstOrDefault(item => item.AssetId == SelectedWorkOrder.AssetId.Value)
                : null;
            var printed = _printService.PrintWorkOrder(SelectedWorkOrder, asset);
            StatusText = printed ? _localization.GetString("StatusWorkOrderPrintSent") : _localization.GetString("StatusPrintCancelled");
        }
        catch (Exception ex)
        {
            StatusText = _localization.GetString("StatusPrintFailed");
            _dialogService.ShowError(_localization.GetString("ErrorPrintFailedTitle"), GetExceptionMessage(ex));
        }
    }
    private async Task AssignSelectedWorkOrderAsync()
    {
        if (SelectedWorkOrder is null) return;
        var assignee = _dialogService.ShowRequiredTextPrompt(
            _localization.GetString("AssignWorkOrder"),
            _localization.GetString("AssignedTo"),
            _localization.GetString("ValidationAssigneeRequired"),
            SelectedWorkOrder.AssignedTo);
        if (assignee is null) return;
        var id = SelectedWorkOrder.WorkOrderId;
        await RunOperationAsync(async () => { await _workOrderService.AssignAsync(id, assignee); await ReloadOperationsAsync(); StatusText = _localization.GetString("StatusWorkOrderAssigned"); });
    }
    private async Task StartSelectedWorkOrderAsync()
    {
        if (SelectedWorkOrder is null) return;
        var id = SelectedWorkOrder.WorkOrderId;
        await RunOperationAsync(async () => { await _workOrderService.StartAsync(id); await ReloadOperationsAsync(); StatusText = _localization.GetString("StatusWorkOrderStarted"); });
    }
    private async Task ResumeSelectedWorkOrderAsync()
    {
        if (SelectedWorkOrder is null) return;
        var id = SelectedWorkOrder.WorkOrderId;
        await RunOperationAsync(async () => { await _workOrderService.ResumeAsync(id); await ReloadOperationsAsync(); StatusText = _localization.GetString("StatusWorkOrderResumed"); });
    }
    private async Task HoldSelectedWorkOrderAsync()
    {
        if (SelectedWorkOrder is null) return;
        var reason = _dialogService.ShowRequiredTextPrompt(
            _localization.GetString("HoldWorkOrder"),
            _localization.GetString("HoldReason"),
            _localization.GetString("ValidationHoldReasonRequired"));
        if (reason is null) return;
        var id = SelectedWorkOrder.WorkOrderId;
        await RunOperationAsync(async () => { await _workOrderService.PutOnHoldAsync(id, reason); await ReloadOperationsAsync(); StatusText = _localization.GetString("StatusWorkOrderOnHold"); });
    }
    private async Task CompleteSelectedWorkOrderAsync()
    {
        if (SelectedWorkOrder is null) return;
        var asset = SelectedWorkOrder.AssetId.HasValue ? _assets.FirstOrDefault(item => item.AssetId == SelectedWorkOrder.AssetId.Value) : null;
        var draft = _dialogService.ShowWorkOrderCompletionDialog(SelectedWorkOrder, asset);
        if (draft is null) return;
        var completedId = SelectedWorkOrder.WorkOrderId;
        await RunOperationAsync(async () =>
        {
            await _workOrderService.CompleteAsync(draft);
            var completed = await _workOrderService.GetByIdAsync(completedId);
            await ReloadAssetAsync(SelectedAsset?.AssetId);
            await ReloadYearsAsync();
            await ReloadOperationsAsync();
            await LoadHistorySafelyAsync();
            StatusText = _localization.GetString("StatusWorkOrderCompleted");
            if (completed is not null) TryAutoPrintWorkOrder(completed);
        });
    }
    private async Task CancelSelectedWorkOrderAsync()
    {
        if (SelectedWorkOrder is null) return;
        var reason = _dialogService.ShowRequiredTextPrompt(
            _localization.GetString("CancelWorkOrder"),
            _localization.GetString("CancellationReason"),
            _localization.GetString("ValidationCancellationReasonRequired"));
        if (reason is null) return;
        var id = SelectedWorkOrder.WorkOrderId;
        await RunOperationAsync(async () => { await _workOrderService.CancelAsync(id, reason); await ReloadOperationsAsync(); StatusText = _localization.GetString("StatusWorkOrderCancelled"); });
    }
    private async Task RefreshWorkOrdersAsync()
    {
        await RunOperationAsync(() => LoadWorkOrderPageAsync(resetPaging: true)).ConfigureAwait(true);
    }

    private async Task NextWorkOrderPageAsync()
    {
        await RunOperationAsync(async () =>
        {
            if (_workOrderNextCursor is null) return;
            var previousCursor = _workOrderPageCursor;
            var nextCursor = _workOrderNextCursor;
            _workOrderPreviousCursors.Push(previousCursor);
            _workOrderPageCursor = nextCursor;
            try
            {
                await LoadWorkOrderPageAsync(resetPaging: false).ConfigureAwait(true);
            }
            catch
            {
                _workOrderPageCursor = previousCursor;
                _ = _workOrderPreviousCursors.Pop();
                throw;
            }
        }).ConfigureAwait(true);
    }

    private async Task PreviousWorkOrderPageAsync()
    {
        await RunOperationAsync(async () =>
        {
            if (_workOrderPreviousCursors.Count == 0) return;
            var currentCursor = _workOrderPageCursor;
            var previousCursor = _workOrderPreviousCursors.Pop();
            _workOrderPageCursor = previousCursor;
            try
            {
                await LoadWorkOrderPageAsync(resetPaging: false).ConfigureAwait(true);
            }
            catch
            {
                _workOrderPageCursor = currentCursor;
                _workOrderPreviousCursors.Push(previousCursor);
                throw;
            }
        }).ConfigureAwait(true);
    }

    private async Task LoadWorkOrderPageAsync(bool resetPaging)
    {
        if (resetPaging)
        {
            _workOrderPageCursor = null;
            _workOrderNextCursor = null;
            _workOrderPreviousCursors.Clear();
        }

        var selectedId = SelectedWorkOrder?.WorkOrderId;
        var query = new WorkOrderQuery(
            Status: IsWorkOrderListCompleted
                ? WorkOrderStatus.Completed
                : IsWorkOrderListCancelled ? WorkOrderStatus.Cancelled : null,
            IsClosed: IsWorkOrderListOpen ? false : null,
            SearchText: WorkOrderSearchText,
            PageSize: 200,
            Cursor: _workOrderPageCursor);
        // Microsoft.Data.Sqlite executes its async ADO.NET calls synchronously. Keep the
        // bounded database page read off the WPF dispatcher, then marshal only 200 rows back.
        var page = await Task.Run(() => _workOrderService.GetPageAsync(query)).ConfigureAwait(true);
        _workOrders.ReplaceAll(page.Items);
        _workOrderNextCursor = page.NextCursor;
        SelectedWorkOrder = selectedId.HasValue
            ? _workOrders.FirstOrDefault(item => item.WorkOrderId == selectedId.Value) ?? _workOrders.FirstOrDefault()
            : _workOrders.FirstOrDefault();
        NotifyWorkOrderPagingChanged();
    }

    private void NotifyWorkOrderPagingChanged()
    {
        OnPropertyChanged(nameof(CanGoToPreviousWorkOrderPage));
        OnPropertyChanged(nameof(CanGoToNextWorkOrderPage));
        PreviousWorkOrderPageCommand.NotifyCanExecuteChanged();
        NextWorkOrderPageCommand.NotifyCanExecuteChanged();
        RefreshWorkOrdersCommand.NotifyCanExecuteChanged();
    }

}
