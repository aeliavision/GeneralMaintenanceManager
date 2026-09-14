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
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        _selectedLanguageCode = _localization.CurrentLanguageCode;
        OnPropertyChanged(nameof(SelectedLanguageCode));
        Languages = CreateLanguageOptions();
        OnPropertyChanged(nameof(SelectedAssetLabel));
        OnPropertyChanged(nameof(AssetCountLabel));
        OnPropertyChanged(nameof(AssetWorkspaceTitle));
        OnPropertyChanged(nameof(AssetWorkspaceSubtitle));
        OnPropertyChanged(nameof(AssetWorkspaceEyebrow));
        OnPropertyChanged(nameof(SelectedAsset));
        AssetView?.Refresh();

        RefreshYearLabels();
        RebuildHistoryPresentation();
        RebuildGlobalHistoryPresentation();
        RebuildReviewPresentation();
        _workOrders.ReplaceAll(_workOrders.ToArray());
        _maintenancePlans.ReplaceAll(_maintenancePlans.ToArray());

        StatusText = AssetCount > 0
            ? _localization.Format("StatusAssetsLoadedFormat", AssetCount)
            : _localization.GetString("StatusReady");
    }
    private void SetSelectedYearWithoutHistoryReload(YearOption year)
    {
        var previousSuppression = _suppressHistoryReload;
        _suppressHistoryReload = true;
        try
        {
            SelectedYear = year;
        }
        finally
        {
            _suppressHistoryReload = previousSuppression;
        }
    }
    private void RefreshYearLabels()
    {
        var selectedYearValue = _selectedYear.Year;
        var yearValues = Years
            .Where(item => item.Year.HasValue)
            .Select(item => item.Year!.Value)
            .ToArray();

        var previousSuppression = _suppressHistoryReload;
        _suppressHistoryReload = true;
        try
        {
            Years.Clear();
            Years.Add(CreateAllYearsOption());
            foreach (var year in yearValues)
            {
                Years.Add(CreateYearOption(year));
            }

            SelectedYear = selectedYearValue is null
                ? Years[0]
                : Years.FirstOrDefault(item => item.Year == selectedYearValue) ?? Years[0];
        }
        finally
        {
            _suppressHistoryReload = previousSuppression;
        }
    }
    private void RebuildHistoryPresentation()
    {
        _history.ReplaceAll(_currentHistoryRecords.Select(maintenanceRecord => new HistoryItemViewModel(maintenanceRecord, _localization)));
        NotifyHistoryPresentationChanged();
    }
    private void RebuildGlobalHistoryPresentation()
    {
        var selectedRecordKey = SelectedGlobalHistoryItem?.RecordKey;
        var localized = _globalHistory.Select(item =>
        {
            if (item.SourceMaintenanceRecord is not null) return new GlobalHistoryItemViewModel(item.SourceMaintenanceRecord, _localization);
            if (item.SourceActivity is not null) return new GlobalHistoryItemViewModel(item.SourceActivity, _localization);
            if (item.SourceWorkOrder is not null) return new GlobalHistoryItemViewModel(item.SourceWorkOrder, _localization);
            if (item.SourceMaintenancePlan is not null) return new GlobalHistoryItemViewModel(item.SourceMaintenancePlan, _localization);
            return item;
        }).ToArray();

        _globalHistory.ReplaceAll(localized);
        OnPropertyChanged(nameof(HistorySiteSuggestions));
        OnPropertyChanged(nameof(HistoryLocationSuggestions));
        OnPropertyChanged(nameof(HistoryMaintenanceTypeSuggestions));
        OnPropertyChanged(nameof(HistoryActivityTypeSuggestions));
        OnPropertyChanged(nameof(HistoryPerformedBySuggestions));
        SelectedGlobalHistoryItem = selectedRecordKey is not null
            ? GlobalHistory.FirstOrDefault(item => string.Equals(item.RecordKey, selectedRecordKey, StringComparison.Ordinal))
            : GlobalHistory.FirstOrDefault();
        GlobalHistoryView?.Refresh();
        RebuildMaintenanceLog();
        RebuildOperationalReport();
        NotifyGlobalHistoryPresentationChanged();
    }
    private void RebuildMaintenanceLog()
    {
        var selectedRecordKey = SelectedMaintenanceLogItem?.RecordKey;
        var search = MaintenanceSearchText.Trim();
        var from = MaintenanceFromDate.HasValue ? DateOnly.FromDateTime(MaintenanceFromDate.Value) : (DateOnly?)null;
        var to = MaintenanceToDate.HasValue ? DateOnly.FromDateTime(MaintenanceToDate.Value) : (DateOnly?)null;
        var rows = _globalHistory
            .Where(item => item.SourceMaintenanceRecord is not null)
            .Where(item => !from.HasValue || item.RecordDate >= from.Value)
            .Where(item => !to.HasValue || item.RecordDate <= to.Value)
            .Where(item => MatchesOptionalFilter(item.Location, MaintenanceLocationFilter))
            .Where(item => MatchesOptionalFilter(item.TypeText, MaintenanceTypeFilter))
            .Where(item => MatchesOptionalFilter(item.PerformedBy, MaintenancePerformedByFilter))
            .Where(item => search.Length == 0
                || Contains(item.Site, search) || Contains(item.Location, search) || Contains(item.Title, search)
                || Contains(item.TypeText, search) || Contains(item.Description, search) || Contains(item.PerformedBy, search)
                || Contains(item.ReferenceNumber, search) || Contains(item.Notes, search))
            .OrderByDescending(item => item.RecordDate)
            .ThenByDescending(item => item.RecordCreatedAtUtc)
            .ToArray();
        _maintenanceLogItems.ReplaceAll(rows);
        SelectedMaintenanceLogItem = selectedRecordKey is not null
            ? _maintenanceLogItems.FirstOrDefault(item => string.Equals(item.RecordKey, selectedRecordKey, StringComparison.Ordinal))
            : _maintenanceLogItems.FirstOrDefault();
        OnPropertyChanged(nameof(MaintenanceLogCount));
        // Do not replace the ItemsSource of an active editable filter. Replacing it while
        // WPF is committing a ComboBox selection clears the editable Text and makes an
        // applied filter look blank. Empty filters can still refresh their suggestions.
        if (MaintenanceLocationFilter.Trim().Length == 0)
            OnPropertyChanged(nameof(MaintenanceLocationSuggestions));
        if (MaintenanceTypeFilter.Trim().Length == 0)
            OnPropertyChanged(nameof(MaintenanceTypeSuggestions));
        if (MaintenancePerformedByFilter.Trim().Length == 0)
            OnPropertyChanged(nameof(MaintenancePerformedBySuggestions));
    }
    private async Task OpenSelectedMaintenanceLogDetailsAsync()
    {
        if (SelectedMaintenanceLogItem is null) return;
        SelectedGlobalHistoryItem = SelectedMaintenanceLogItem;
        await OpenSelectedGlobalHistoryDetailsAsync().ConfigureAwait(true);
        if (IsMaintenanceView) await LoadGlobalHistorySafelyAsync().ConfigureAwait(true);
    }
    private void RebuildOperationalReport()
    {
        var search = OperationalReportSearchText.Trim();
        var from = OperationalReportFromDate.HasValue ? DateOnly.FromDateTime(OperationalReportFromDate.Value) : (DateOnly?)null;
        var to = OperationalReportToDate.HasValue ? DateOnly.FromDateTime(OperationalReportToDate.Value) : (DateOnly?)null;
        var rows = _globalHistory
            .Where(item => item.SourceMaintenanceRecord is not null)
            .Where(item => !from.HasValue || item.RecordDate >= from.Value)
            .Where(item => !to.HasValue || item.RecordDate <= to.Value)
            .Where(item => search.Length == 0
                || Contains(item.Site, search) || Contains(item.Location, search) || Contains(item.Title, search)
                || Contains(item.TypeText, search) || Contains(item.Description, search) || Contains(item.PerformedBy, search)
                || Contains(item.ReferenceNumber, search) || Contains(item.Notes, search))
            .OrderByDescending(item => item.RecordDate)
            .ThenByDescending(item => item.RecordCreatedAtUtc)
            .ToArray();
        _operationalReportItems.ReplaceAll(rows);
        OnPropertyChanged(nameof(OperationalReportCount));
        OnPropertyChanged(nameof(OperationalReportCost));
        OnPropertyChanged(nameof(OperationalReportLocationCount));
        OnPropertyChanged(nameof(OperationalReportAverageCost));
        OnPropertyChanged(nameof(OperationalReportTopLocations));
        OnPropertyChanged(nameof(OperationalReportTopMaintenanceTypes));
        PrintOperationalReportCommand.NotifyCanExecuteChanged();
    }
    private void PrintOperationalReport()
    {
        if (_operationalReportItems.Count == 0) return;
        try
        {
            var scope = string.Join(" · ", new[]
            {
                OperationalReportFromDate?.ToString("d", _localization.CurrentCulture),
                OperationalReportToDate?.ToString("d", _localization.CurrentCulture),
                OperationalReportSearchText.Trim()
            }.Where(value => !string.IsNullOrWhiteSpace(value)));
            if (scope.Length == 0) scope = _localization.GetString("YearAllYears");
            var printed = _printService.PrintGlobalHistory(_operationalReportItems.ToArray(), scope);
            StatusText = printed ? _localization.GetString("StatusGlobalHistoryPrintSent") : _localization.GetString("StatusPrintCancelled");
        }
        catch (Exception ex)
        {
            StatusText = _localization.GetString("StatusPrintFailed");
            _dialogService.ShowError(_localization.GetString("ErrorPrintFailedTitle"), GetExceptionMessage(ex));
        }
    }
    private void NotifyGlobalHistoryPresentationChanged()
    {
        OnPropertyChanged(nameof(GlobalHistoryCount));
        OnPropertyChanged(nameof(GlobalHistoryAssetCount));
        OnPropertyChanged(nameof(GlobalHistoryCost));
        PrintGlobalHistoryCommand.NotifyCanExecuteChanged();
        NotifyUnifiedReportPresentationChanged();
    }
    private void NotifyUnifiedReportPresentationChanged()
    {
        OnPropertyChanged(nameof(UnifiedReportLocationCount));
        OnPropertyChanged(nameof(UnifiedReportPeopleCount));
        OnPropertyChanged(nameof(UnifiedReportCost));
        OnPropertyChanged(nameof(UnifiedReportTopLocations));
        OnPropertyChanged(nameof(UnifiedReportTopTypes));
    }
    private void NotifyHistoryPresentationChanged()
    {
        OnPropertyChanged(nameof(HistoryCount));
        OnPropertyChanged(nameof(HistoryCountLabel));
    }
}
