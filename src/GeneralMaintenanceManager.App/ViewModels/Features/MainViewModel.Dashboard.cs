using GeneralMaintenanceManager.Core.Enums;
using GeneralMaintenanceManager.Core.Models;
namespace GeneralMaintenanceManager.App.ViewModels;

public sealed partial class MainViewModel
{
    private async Task ReloadOperationsAsync()
    {
        // Any successful operation refresh may follow a Maintenance/Work Order mutation.
        // Keep startup lean, but force the next entry dialog to rebuild its lazy snapshot.
        _maintenanceSuggestionsLoaded = false;
        _workOrderSuggestionsLoaded = false;

        // Dashboard data is intentionally lean: no exhaustive Maintenance windows and no
        // cross-year suggestion catalogs are touched during normal startup/refresh. SQLite
        // reads are dispatched away from the WPF thread and independent databases/queries
        // are allowed to overlap. Only their bounded results are applied to UI collections.
        var currentYear = DateTime.Today.Year;
        var workOrderQuery = IsWorkOrdersView
            ? new WorkOrderQuery(
                Status: IsWorkOrderListCompleted
                    ? WorkOrderStatus.Completed
                    : IsWorkOrderListCancelled ? WorkOrderStatus.Cancelled : null,
                IsClosed: IsWorkOrderListOpen ? false : null,
                SearchText: WorkOrderSearchText,
                PageSize: 200)
            : new WorkOrderQuery(PageSize: 200);
        var dashboardTask = Task.Run(() => _maintenanceRecordService.GetDashboardSummaryAsync(currentYear));
        var workOrderPageTask = Task.Run(() => _workOrderService.GetPageAsync(workOrderQuery));
        var workOrderMetricsTask = Task.Run(() => _workOrderService.GetDashboardMetricsAsync());

        _dashboardSummary = await dashboardTask.ConfigureAwait(true);
        var workOrderPage = await workOrderPageTask.ConfigureAwait(true);
        _workOrderDashboardMetrics = await workOrderMetricsTask.ConfigureAwait(true);
        _workOrderPageCursor = null;
        _workOrderNextCursor = workOrderPage.NextCursor;
        _workOrderPreviousCursors.Clear();
        _workOrders.ReplaceAll(workOrderPage.Items);
        SelectedWorkOrder = SelectedWorkOrder is null
            ? _workOrders.FirstOrDefault()
            : _workOrders.FirstOrDefault(item => item.WorkOrderId == SelectedWorkOrder.WorkOrderId) ?? _workOrders.FirstOrDefault();
        NotifyWorkOrderPagingChanged();

        if (IsPreventiveMaintenanceEnabled)
        {
            _dueMaintenancePlanCount = await _maintenancePlanService
                .GetDueCountAsync(DateOnly.FromDateTime(DateTime.Today))
                .ConfigureAwait(true);
            if (IsPreventiveView)
                await LoadPreventiveWorkspaceAsync().ConfigureAwait(true);
        }
        else
        {
            _dueMaintenancePlanCount = 0;
            _maintenancePlans.Clear();
            SelectedMaintenancePlan = null;
        }

        OnPropertyChanged(nameof(MaintenanceThisMonthCount));
        OnPropertyChanged(nameof(MaintenanceCostThisMonth));
        OnPropertyChanged(nameof(RecentMaintenanceRecords));
        OnPropertyChanged(nameof(RecentMaintenanceSummaryItems));
        OnPropertyChanged(nameof(DashboardTopLocations));
        OnPropertyChanged(nameof(DashboardTopMaintenanceTypes));
        OnPropertyChanged(nameof(OpenWorkOrderCount));
        OnPropertyChanged(nameof(UrgentWorkOrderCount));
        OnPropertyChanged(nameof(OverdueWorkOrderCount));
        OnPropertyChanged(nameof(DueMaintenancePlanCount));
    }

    private async Task ReloadOperationsAfterFeatureChangeAsync()
    {
        try
        {
            await ReloadOperationsAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _diagnosticLogger.LogError("OptionalFeatureRefresh", ex, "Optional feature refresh failed; the existing UI state was preserved.");
        }
    }
}
