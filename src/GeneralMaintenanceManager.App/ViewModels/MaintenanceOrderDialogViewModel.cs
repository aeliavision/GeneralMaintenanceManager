using CommunityToolkit.Mvvm.ComponentModel;
using GeneralMaintenanceManager.App.Models;
using GeneralMaintenanceManager.App.Services;
using GeneralMaintenanceManager.Core.Enums;
using GeneralMaintenanceManager.Core.Models;

namespace GeneralMaintenanceManager.App.ViewModels;

public sealed class MaintenanceOrderDialogViewModel : ObservableObject
{
    private readonly ILocalizationService _localization;
    private string _site = string.Empty;
    private string _location = string.Empty;
    private string _subject = string.Empty;
    private string _maintenanceType;
    private string _problem = string.Empty;
    private ChoiceOption<WorkOrderPriority> _selectedPriority;
    private string _requestedBy = string.Empty;
    private DateTime? _dueDate;
    private string _assignedTo = string.Empty;
    private string _notes = string.Empty;
    private string _validationMessage = string.Empty;
    private AssetChoice _selectedAsset;

    public MaintenanceOrderDialogViewModel(
        MaintenanceEntryOptions options,
        GeneralMaintenanceManager.Core.Entities.Asset? preferredAsset,
        ILocalizationService localization)
    {
        ArgumentNullException.ThrowIfNull(options);
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));

        Sites = Normalize(options.Sites);
        Locations = Normalize(options.Locations);
        Subjects = Normalize(options.Subjects);
        AssetTrackingEnabled = options.AssetTrackingEnabled;
        Assets =
        [
            new AssetChoice(null, localization.GetString("NoAsset"), string.Empty, string.Empty, "General", string.Empty, string.Empty),
            .. options.Assets.OrderBy(item => item.AssetNumber).ThenBy(item => item.AssetName)
                .Select(item => new AssetChoice(
                    item.AssetId,
                    string.IsNullOrWhiteSpace(item.AssetNumber) ? item.AssetName : $"#{item.AssetNumber} - {item.AssetName}",
                    item.Site,
                    item.Location,
                    item.AssetCategory,
                    item.Condition,
                    item.OperationalStatus))
        ];
        _selectedAsset = preferredAsset is null
            ? Assets[0]
            : Assets.FirstOrDefault(item => item.AssetId == preferredAsset.AssetId) ?? Assets[0];

        var maintenanceTypeSource = options.MaintenanceTypes.Count > 0 ? options.MaintenanceTypes : MaintenanceTypeCatalog.BuiltIn;
        MaintenanceTypes = MaintenanceTextPresentation.LocalizeMaintenanceTypes(maintenanceTypeSource, localization);
        _maintenanceType = MaintenanceTypes.Count > 0 ? MaintenanceTypes[0] : localization.GetString("MaintenanceTypeGeneral");

        Priorities =
        [
            new(WorkOrderPriority.Low, localization.GetString("PriorityLow")),
            new(WorkOrderPriority.Normal, localization.GetString("PriorityNormal")),
            new(WorkOrderPriority.High, localization.GetString("PriorityHigh")),
            new(WorkOrderPriority.Urgent, localization.GetString("PriorityUrgent"))
        ];
        _selectedPriority = Priorities.First(item => item.Value == WorkOrderPriority.Normal);

        if (_selectedAsset.AssetId.HasValue)
        {
            Site = _selectedAsset.Site;
            Location = _selectedAsset.Location;
        }
    }

    public IReadOnlyList<string> Sites { get; }
    public IReadOnlyList<string> Locations { get; }
    public IReadOnlyList<string> Subjects { get; }
    public IReadOnlyList<string> MaintenanceTypes { get; }
    public IReadOnlyList<AssetChoice> Assets { get; }
    public IReadOnlyList<ChoiceOption<WorkOrderPriority>> Priorities { get; }
    public bool AssetTrackingEnabled { get; }

    public AssetChoice SelectedAsset
    {
        get => _selectedAsset;
        set
        {
            if (!SetProperty(ref _selectedAsset, value)) return;
            if (!value.AssetId.HasValue) return;
            if (string.IsNullOrWhiteSpace(Site)) Site = value.Site;
            if (string.IsNullOrWhiteSpace(Location)) Location = value.Location;
        }
    }

    public string DialogTitle => _localization.GetString("NewMaintenanceOrder");
    public string SaveButtonText => _localization.GetString("CreateMaintenanceOrder");
    public string Site { get => _site; set => SetProperty(ref _site, value ?? string.Empty); }
    public string Location { get => _location; set => SetProperty(ref _location, value ?? string.Empty); }
    public string Subject { get => _subject; set => SetProperty(ref _subject, value ?? string.Empty); }
    public string MaintenanceType { get => _maintenanceType; set => SetProperty(ref _maintenanceType, value ?? string.Empty); }
    public string Problem { get => _problem; set => SetProperty(ref _problem, value ?? string.Empty); }
    public ChoiceOption<WorkOrderPriority> SelectedPriority { get => _selectedPriority; set => SetProperty(ref _selectedPriority, value); }
    public string RequestedBy { get => _requestedBy; set => SetProperty(ref _requestedBy, value ?? string.Empty); }
    public DateTime? DueDate { get => _dueDate; set => SetProperty(ref _dueDate, value); }
    public string AssignedTo { get => _assignedTo; set => SetProperty(ref _assignedTo, value ?? string.Empty); }
    public string Notes { get => _notes; set => SetProperty(ref _notes, value ?? string.Empty); }
    public string ValidationMessage { get => _validationMessage; private set => SetProperty(ref _validationMessage, value ?? string.Empty); }

    public bool TryCreateDraft(out WorkOrderDraft? draft)
    {
        draft = null;
        ValidationMessage = string.Empty;

        var location = Location.Trim();
        var subject = Subject.Trim();
        var problem = Problem.Trim();
        var canonicalType = MaintenanceTextPresentation.ResolveCanonicalMaintenanceType(MaintenanceType, _localization);

        if (location.Length == 0)
        {
            ValidationMessage = _localization.GetString("ValidationMaintenanceLocationRequired");
            return false;
        }
        if (subject.Length == 0)
        {
            ValidationMessage = _localization.GetString("ValidationMaintenanceSubjectRequired");
            return false;
        }
        if (canonicalType.Length == 0)
        {
            ValidationMessage = _localization.GetString("ValidationMaintenanceTypeRequired");
            return false;
        }
        if (problem.Length == 0)
        {
            ValidationMessage = _localization.GetString("ValidationProblemRequired");
            return false;
        }

        draft = new WorkOrderDraft(
            AssetTrackingEnabled ? SelectedAsset.AssetId : null,
            null,
            Site.Trim(),
            location,
            subject,
            canonicalType,
            ResolveWorkOrderMaintenanceType(canonicalType),
            SelectedPriority.Value,
            problem,
            RequestedBy.Trim(),
            DueDate.HasValue ? DateOnly.FromDateTime(DueDate.Value) : null,
            AssignedTo.Trim(),
            Notes.Trim());
        return true;
    }

    private static MaintenanceType ResolveWorkOrderMaintenanceType(string canonicalType) => canonicalType switch
    {
        "Repair" => global::GeneralMaintenanceManager.Core.Enums.MaintenanceType.CorrectiveMaintenance,
        "Service" => global::GeneralMaintenanceManager.Core.Enums.MaintenanceType.RoutineService,
        "Inspection" => global::GeneralMaintenanceManager.Core.Enums.MaintenanceType.Inspection,
        "Installation" => global::GeneralMaintenanceManager.Core.Enums.MaintenanceType.Installation,
        "Replacement" => global::GeneralMaintenanceManager.Core.Enums.MaintenanceType.Replacement,
        "Test / Calibration" => global::GeneralMaintenanceManager.Core.Enums.MaintenanceType.TestOrCalibration,
        _ => global::GeneralMaintenanceManager.Core.Enums.MaintenanceType.GeneralMaintenance
    };

    private static string[] Normalize(IEnumerable<string> source) => source
        .Select(item => item?.Trim() ?? string.Empty)
        .Where(item => item.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(item => item, StringComparer.CurrentCultureIgnoreCase)
        .ToArray();
}
