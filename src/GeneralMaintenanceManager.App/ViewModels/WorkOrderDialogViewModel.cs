using CommunityToolkit.Mvvm.ComponentModel;
using GeneralMaintenanceManager.App.Models;
using GeneralMaintenanceManager.App.Services;
using GeneralMaintenanceManager.Core.Entities;
using GeneralMaintenanceManager.Core.Enums;
using GeneralMaintenanceManager.Core.Models;

namespace GeneralMaintenanceManager.App.ViewModels;

public sealed class WorkOrderDialogViewModel : ObservableObject
{
    private readonly ILocalizationService _localization;
    private readonly Guid? _assetId;
    private readonly Guid? _maintenancePlanId;
    private readonly string _maintenanceCategory;
    private readonly MaintenanceType _maintenanceType;
    private string _site = string.Empty;
    private string _location = string.Empty;
    private string _title = string.Empty;
    private ChoiceOption<WorkOrderPriority> _selectedPriority;
    private string _problem = string.Empty;
    private string _notes = string.Empty;
    private string _requestedBy = string.Empty;
    private DateTime? _dueDate;
    private string _assignedTo = string.Empty;
    private string _validationMessage = string.Empty;

    public WorkOrderDialogViewModel(IReadOnlyList<Asset> assets, Asset? preferredAsset, ILocalizationService localization)
        : this(assets, existing: null, localization, initialize: true)
    {
        if (preferredAsset is not null)
        {
            Site = preferredAsset.Site;
            Location = preferredAsset.Location;
        }
    }

    public WorkOrderDialogViewModel(IReadOnlyList<Asset> assets, WorkOrder existing, ILocalizationService localization)
        : this(assets, existing ?? throw new ArgumentNullException(nameof(existing)), localization, initialize: true)
    {
    }

    private WorkOrderDialogViewModel(IReadOnlyList<Asset> assets, WorkOrder? existing, ILocalizationService localization, bool initialize)
    {
        _ = initialize;
        ArgumentNullException.ThrowIfNull(assets);
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        Priorities =
        [
            new(WorkOrderPriority.Low, localization.GetString("PriorityLow")),
            new(WorkOrderPriority.Normal, localization.GetString("PriorityNormal")),
            new(WorkOrderPriority.High, localization.GetString("PriorityHigh")),
            new(WorkOrderPriority.Urgent, localization.GetString("PriorityUrgent"))
        ];
        _selectedPriority = Priorities.First(item => item.Value == WorkOrderPriority.Normal);
        IsEditMode = existing is not null;
        DialogTitle = localization.GetString(IsEditMode ? "EditWorkOrder" : "NewWorkOrder");
        SaveButtonText = localization.GetString(IsEditMode ? "SaveWorkOrder" : "CreateWorkOrder");
        _assetId = existing?.AssetId;
        _maintenancePlanId = existing?.MaintenancePlanId;
        _maintenanceCategory = existing?.MaintenanceCategory ?? string.Empty;
        _maintenanceType = existing?.MaintenanceType ?? MaintenanceType.GeneralMaintenance;

        if (existing is not null)
        {
            Site = existing.Site;
            Location = existing.Location;
            Title = existing.Title;
            SelectedPriority = Priorities.FirstOrDefault(item => item.Value == existing.Priority) ?? _selectedPriority;
            Problem = existing.ProblemDescription;
            Notes = existing.Notes;
            RequestedBy = existing.RequestedBy;
            DueDate = existing.DueDate?.ToDateTime(TimeOnly.MinValue);
            AssignedTo = existing.AssignedTo;
        }
    }

    public IReadOnlyList<ChoiceOption<WorkOrderPriority>> Priorities { get; }
    public bool IsEditMode { get; }
    public string DialogTitle { get; }
    public string SaveButtonText { get; }
    public string Site { get => _site; set => SetProperty(ref _site, value ?? string.Empty); }
    public string Location { get => _location; set => SetProperty(ref _location, value ?? string.Empty); }
    public string Title { get => _title; set => SetProperty(ref _title, value ?? string.Empty); }
    public ChoiceOption<WorkOrderPriority> SelectedPriority { get => _selectedPriority; set => SetProperty(ref _selectedPriority, value); }
    public string Problem { get => _problem; set => SetProperty(ref _problem, value ?? string.Empty); }
    public string Notes { get => _notes; set => SetProperty(ref _notes, value ?? string.Empty); }
    public string RequestedBy { get => _requestedBy; set => SetProperty(ref _requestedBy, value ?? string.Empty); }
    public DateTime? DueDate { get => _dueDate; set => SetProperty(ref _dueDate, value); }
    public string AssignedTo { get => _assignedTo; set => SetProperty(ref _assignedTo, value ?? string.Empty); }
    public string ValidationMessage { get => _validationMessage; private set => SetProperty(ref _validationMessage, value); }

    public bool TryCreateDraft(out WorkOrderDraft? draft)
    {
        draft = null;
        ValidationMessage = string.Empty;
        if (string.IsNullOrWhiteSpace(Location))
        {
            ValidationMessage = _localization.GetString("ValidationWorkOrderLocationRequired");
            return false;
        }
        if (string.IsNullOrWhiteSpace(Title))
        {
            ValidationMessage = _localization.GetString("ValidationWorkOrderTitleRequired");
            return false;
        }
        if (string.IsNullOrWhiteSpace(Problem))
        {
            ValidationMessage = _localization.GetString("ValidationProblemRequired");
            return false;
        }

        draft = new WorkOrderDraft(
            _assetId,
            _maintenancePlanId,
            Site.Trim(),
            Location.Trim(),
            Title.Trim(),
            _maintenanceCategory,
            _maintenanceType,
            SelectedPriority.Value,
            Problem.Trim(),
            RequestedBy.Trim(),
            DueDate.HasValue ? DateOnly.FromDateTime(DueDate.Value) : null,
            AssignedTo.Trim(),
            Notes.Trim());
        return true;
    }
}
