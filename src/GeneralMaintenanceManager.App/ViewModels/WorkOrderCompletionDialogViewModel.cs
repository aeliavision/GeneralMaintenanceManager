using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using GeneralMaintenanceManager.App.Services;
using GeneralMaintenanceManager.Core.Entities;
using GeneralMaintenanceManager.Core.Models;

namespace GeneralMaintenanceManager.App.ViewModels;

public sealed class WorkOrderCompletionDialogViewModel : ObservableObject
{
    private readonly ILocalizationService _localization;
    private readonly Guid _workOrderId;
    private string _workPerformed = string.Empty;
    private string _performedBy = string.Empty;
    private string _costText = string.Empty;
    private string _downtimeText = string.Empty;
    private string _materials = string.Empty;
    private string _conditionAfter;
    private string _operationalStatusAfter;
    private string _completionNotes = string.Empty;
    private string _validationMessage = string.Empty;

    public WorkOrderCompletionDialogViewModel(WorkOrder order, Asset? asset, ILocalizationService localization)
    {
        _localization = localization;
        _workOrderId = order.WorkOrderId;
        WorkOrderLabel = order.WorkOrderNumber;
        Title = order.Title;
        _performedBy = order.AssignedTo;
        _conditionAfter = asset?.Condition ?? string.Empty;
        _operationalStatusAfter = asset?.OperationalStatus ?? string.Empty;
    }

    public string WorkOrderLabel { get; }
    public string Title { get; }
    public string WorkPerformed { get => _workPerformed; set => SetProperty(ref _workPerformed, value ?? string.Empty); }
    public string PerformedBy { get => _performedBy; set => SetProperty(ref _performedBy, value ?? string.Empty); }
    public string CostText { get => _costText; set => SetProperty(ref _costText, value ?? string.Empty); }
    public string DowntimeText { get => _downtimeText; set => SetProperty(ref _downtimeText, value ?? string.Empty); }
    public string Materials { get => _materials; set => SetProperty(ref _materials, value ?? string.Empty); }
    public string ConditionAfter { get => _conditionAfter; set => SetProperty(ref _conditionAfter, value ?? string.Empty); }
    public string OperationalStatusAfter { get => _operationalStatusAfter; set => SetProperty(ref _operationalStatusAfter, value ?? string.Empty); }
    public string CompletionNotes { get => _completionNotes; set => SetProperty(ref _completionNotes, value ?? string.Empty); }
    public string ValidationMessage { get => _validationMessage; private set => SetProperty(ref _validationMessage, value); }

    public bool TryCreateDraft(out WorkOrderCompletionDraft? draft)
    {
        draft = null;
        ValidationMessage = string.Empty;
        if (string.IsNullOrWhiteSpace(WorkPerformed))
        {
            ValidationMessage = _localization.GetString("ValidationWorkPerformedRequired");
            return false;
        }
        if (!TryParseOptionalDecimal(CostText, out var cost) || cost < 0)
        {
            ValidationMessage = _localization.GetString("ValidationCostInvalid");
            return false;
        }
        if (!TryParseOptionalDecimal(DowntimeText, out var downtime) || downtime < 0)
        {
            ValidationMessage = _localization.GetString("ValidationDowntimeInvalid");
            return false;
        }
        draft = new WorkOrderCompletionDraft(_workOrderId, WorkPerformed.Trim(), PerformedBy.Trim(), cost, downtime,
            Materials.Trim(), ConditionAfter.Trim(), OperationalStatusAfter.Trim(), CompletionNotes.Trim());
        return true;
    }

    private bool TryParseOptionalDecimal(string value, out decimal? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (decimal.TryParse(value.Trim(), NumberStyles.Number, _localization.CurrentCulture, out var parsed) ||
            decimal.TryParse(value.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out parsed))
        {
            result = parsed;
            return true;
        }
        return false;
    }
}
