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
    private async Task NewMaintenancePlanAsync()
    {
        var draft = _dialogService.ShowMaintenancePlanDialog(_assets.ToArray(), preferredAsset: null);
        if (draft is null) return;
        await RunOperationAsync(async () => { await _maintenancePlanService.CreateAsync(draft); await ReloadOperationsAsync(); SetActiveView("Preventive"); StatusText = _localization.GetString("StatusMaintenancePlanCreated"); });
    }
    private async Task EditSelectedMaintenancePlanAsync()
    {
        if (SelectedMaintenancePlan is null) return;
        var selectedId = SelectedMaintenancePlan.MaintenancePlanId;
        var draft = _dialogService.ShowMaintenancePlanEditDialog(_assets.ToArray(), SelectedMaintenancePlan);
        if (draft is null) return;
        await RunOperationAsync(async () =>
        {
            await _maintenancePlanService.UpdateAsync(selectedId, draft);
            await ReloadOperationsAsync();
            SelectedMaintenancePlan = _maintenancePlans.FirstOrDefault(item => item.MaintenancePlanId == selectedId);
            StatusText = _localization.GetString("StatusMaintenancePlanUpdated");
        });
    }
    private async Task GenerateSelectedPlanWorkOrderAsync()
    {
        if (SelectedMaintenancePlan is null) return;
        var id = SelectedMaintenancePlan.MaintenancePlanId;
        await RunOperationAsync(async () => { await _maintenancePlanService.GenerateWorkOrderAsync(id); InvalidateEntrySuggestionCaches(); SetWorkOrderListMode("Open"); SetActiveView("WorkOrders"); await ReloadOperationsAsync(); StatusText = _localization.GetString("StatusPlanWorkOrderCreated"); });
    }
    private async Task ToggleSelectedPlanActiveAsync()
    {
        if (SelectedMaintenancePlan is null) return;
        var id = SelectedMaintenancePlan.MaintenancePlanId;
        var active = !SelectedMaintenancePlan.IsActive;
        await RunOperationAsync(async () => { await _maintenancePlanService.SetActiveAsync(id, active); await ReloadOperationsAsync(); StatusText = _localization.GetString(active ? "StatusMaintenancePlanActivated" : "StatusMaintenancePlanPaused"); });
    }
    private async Task PrintSelectedMaintenancePlanAsync()
    {
        if (SelectedMaintenancePlan is null) return;
        try
        {
            var activities = await _activityHistoryService.GetForRecordAsync(
                ActivityRecordTypes.PreventiveMaintenance, SelectedMaintenancePlan.MaintenancePlanId).ConfigureAwait(true);
            var printed = _printService.PrintMaintenancePlan(SelectedMaintenancePlan, activities);
            StatusText = printed ? _localization.GetString("StatusPreventivePrintSent") : _localization.GetString("StatusPrintCancelled");
        }
        catch (Exception ex)
        {
            StatusText = _localization.GetString("StatusPrintFailed");
            _dialogService.ShowError(_localization.GetString("ErrorPrintFailedTitle"), GetExceptionMessage(ex));
        }
    }

    private async Task ShowPreventiveAsync()
    {
        SetActiveView("Preventive");
        await RunOperationAsync(LoadPreventiveWorkspaceAsync).ConfigureAwait(true);
    }

    private async Task LoadPreventiveWorkspaceAsync()
    {
        if (!IsPreventiveMaintenanceEnabled) return;
        var selectedId = SelectedMaintenancePlan?.MaintenancePlanId;
        var today = DateOnly.FromDateTime(DateTime.Today);
        await _maintenancePlanService.ProcessDueActivitiesAsync(today).ConfigureAwait(true);
        var plans = await _maintenancePlanService.GetAllAsync().ConfigureAwait(true);
        _maintenancePlans.ReplaceAll(plans);
        SelectedMaintenancePlan = selectedId.HasValue
            ? _maintenancePlans.FirstOrDefault(item => item.MaintenancePlanId == selectedId.Value) ?? _maintenancePlans.FirstOrDefault()
            : _maintenancePlans.FirstOrDefault();
        _dueMaintenancePlanCount = await _maintenancePlanService.GetDueCountAsync(today).ConfigureAwait(true);
        OnPropertyChanged(nameof(DueMaintenancePlanCount));
    }
}
