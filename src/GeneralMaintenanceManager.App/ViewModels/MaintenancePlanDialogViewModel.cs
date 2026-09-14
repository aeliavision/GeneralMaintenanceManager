using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using GeneralMaintenanceManager.App.Models;
using GeneralMaintenanceManager.App.Services;
using GeneralMaintenanceManager.Core.Entities;
using GeneralMaintenanceManager.Core.Enums;
using GeneralMaintenanceManager.Core.Models;

namespace GeneralMaintenanceManager.App.ViewModels;

public sealed class MaintenancePlanDialogViewModel : ObservableObject
{
    private readonly ILocalizationService _localization;
    private readonly bool _isActive;
    private AssetChoice _selectedAsset;
    private string _site = string.Empty;
    private string _location = string.Empty;
    private string _planName = string.Empty;
    private string _category = string.Empty;
    private ChoiceOption<MaintenanceType> _selectedType;
    private string _instructions = string.Empty;
    private string _frequencyText = "1";
    private ChoiceOption<MaintenanceFrequencyUnit> _selectedUnit;
    private DateTime? _nextDueDate = DateTime.Today.AddMonths(1);
    private string _assignedTo = string.Empty;
    private string _estimatedCostText = string.Empty;
    private string _validationMessage = string.Empty;

    public MaintenancePlanDialogViewModel(IReadOnlyList<Asset> assets, Asset? preferredAsset, bool showAssetLink, ILocalizationService localization)
        : this(assets, preferredAsset, existing: null, showAssetLink, localization)
    {
    }

    public MaintenancePlanDialogViewModel(IReadOnlyList<Asset> assets, MaintenancePlan existing, bool showAssetLink, ILocalizationService localization)
        : this(assets, null, existing ?? throw new ArgumentNullException(nameof(existing)), showAssetLink, localization)
    {
    }

    private MaintenancePlanDialogViewModel(
        IReadOnlyList<Asset> assets,
        Asset? preferredAsset,
        MaintenancePlan? existing,
        bool showAssetLink,
        ILocalizationService localization)
    {
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        ShowAssetLink = showAssetLink;
        ArgumentNullException.ThrowIfNull(assets);

        Assets = [new AssetChoice(null, localization.GetString("LocationOnlyMaintenancePlan"), string.Empty, string.Empty, "General", string.Empty, string.Empty),
            .. assets.OrderBy(item => item.AssetNumber).ThenBy(item => item.AssetName)
                .Select(item => new AssetChoice(item.AssetId,
                    string.IsNullOrWhiteSpace(item.AssetNumber) ? item.AssetName : $"#{item.AssetNumber} - {item.AssetName}",
                    item.Site, item.Location, string.IsNullOrWhiteSpace(item.AssetCategory) ? "General" : item.AssetCategory, item.Condition, item.OperationalStatus))];
        MaintenanceTypes = MaintenanceTypePresentation.CreateMaintenanceTypeChoices(localization);
        FrequencyUnits =
        [
            new(MaintenanceFrequencyUnit.Days, localization.GetString("FrequencyDays")),
            new(MaintenanceFrequencyUnit.Weeks, localization.GetString("FrequencyWeeks")),
            new(MaintenanceFrequencyUnit.Months, localization.GetString("FrequencyMonths")),
            new(MaintenanceFrequencyUnit.Years, localization.GetString("FrequencyYears"))
        ];

        IsEditMode = existing is not null;
        DialogTitle = localization.GetString(IsEditMode ? "EditMaintenancePlan" : "NewMaintenancePlan");
        SaveButtonText = localization.GetString(IsEditMode ? "SavePlan" : "CreatePlan");
        _isActive = existing?.IsActive ?? true;

        _selectedType = MaintenanceTypes.First(item => item.Value == MaintenanceType.PreventiveMaintenance);
        _selectedUnit = FrequencyUnits.First(item => item.Value == MaintenanceFrequencyUnit.Months);

        if (existing is not null)
        {
            _selectedAsset = existing.AssetId.HasValue
                ? Assets.FirstOrDefault(item => item.AssetId == existing.AssetId.Value) ?? Assets[0]
                : Assets[0];
            ApplySelectedAsset();

            Site = existing.Site;
            Location = existing.Location;
            PlanName = existing.PlanName;
            Category = existing.MaintenanceCategory;
            SelectedType = MaintenanceTypes.FirstOrDefault(item => item.Value == existing.MaintenanceType) ?? _selectedType;
            Instructions = existing.Instructions;
            FrequencyText = existing.FrequencyValue.ToString(localization.CurrentCulture);
            SelectedUnit = FrequencyUnits.FirstOrDefault(item => item.Value == existing.FrequencyUnit) ?? _selectedUnit;
            NextDueDate = existing.NextDueDate.ToDateTime(TimeOnly.MinValue);
            AssignedTo = existing.AssignedTo;
            EstimatedCostText = existing.EstimatedCost?.ToString("N2", localization.CurrentCulture) ?? string.Empty;
        }
        else
        {
            _selectedAsset = preferredAsset is null ? Assets[0] : Assets.FirstOrDefault(item => item.AssetId == preferredAsset.AssetId) ?? Assets[0];
            ApplySelectedAsset();
        }
    }

    public IReadOnlyList<AssetChoice> Assets { get; }
    public bool ShowAssetLink { get; }
    public IReadOnlyList<ChoiceOption<MaintenanceType>> MaintenanceTypes { get; }
    public IReadOnlyList<ChoiceOption<MaintenanceFrequencyUnit>> FrequencyUnits { get; }
    public bool IsEditMode { get; }
    public string DialogTitle { get; }
    public string SaveButtonText { get; }
    public AssetChoice SelectedAsset { get => _selectedAsset; set { if (SetProperty(ref _selectedAsset, value)) ApplySelectedAsset(); } }
    public string Site { get => _site; set => SetProperty(ref _site, value ?? string.Empty); }
    public string Location { get => _location; set => SetProperty(ref _location, value ?? string.Empty); }
    public string PlanName { get => _planName; set => SetProperty(ref _planName, value ?? string.Empty); }
    public string Category { get => _category; set => SetProperty(ref _category, value ?? string.Empty); }
    public ChoiceOption<MaintenanceType> SelectedType { get => _selectedType; set => SetProperty(ref _selectedType, value); }
    public string Instructions { get => _instructions; set => SetProperty(ref _instructions, value ?? string.Empty); }
    public string FrequencyText { get => _frequencyText; set => SetProperty(ref _frequencyText, value ?? string.Empty); }
    public ChoiceOption<MaintenanceFrequencyUnit> SelectedUnit { get => _selectedUnit; set => SetProperty(ref _selectedUnit, value); }
    public DateTime? NextDueDate { get => _nextDueDate; set => SetProperty(ref _nextDueDate, value); }
    public string AssignedTo { get => _assignedTo; set => SetProperty(ref _assignedTo, value ?? string.Empty); }
    public string EstimatedCostText { get => _estimatedCostText; set => SetProperty(ref _estimatedCostText, value ?? string.Empty); }
    public string ValidationMessage { get => _validationMessage; private set => SetProperty(ref _validationMessage, value); }

    public bool TryCreateDraft(out MaintenancePlanDraft? draft)
    {
        draft = null;
        ValidationMessage = string.Empty;
        if (string.IsNullOrWhiteSpace(Location)) { ValidationMessage = _localization.GetString("ValidationMaintenanceLocationRequired"); return false; }
        if (string.IsNullOrWhiteSpace(PlanName)) { ValidationMessage = _localization.GetString("ValidationMaintenanceSubjectRequired"); return false; }
        if (string.IsNullOrWhiteSpace(Instructions)) { ValidationMessage = _localization.GetString("ValidationPreventiveTaskRequired"); return false; }
        if (!int.TryParse(FrequencyText.Trim(), out var frequency) || frequency <= 0) { ValidationMessage = _localization.GetString("ValidationFrequencyInvalid"); return false; }
        if (!NextDueDate.HasValue) { ValidationMessage = _localization.GetString("ValidationNextDueRequired"); return false; }
        decimal? estimatedCost = null;
        if (!string.IsNullOrWhiteSpace(EstimatedCostText))
        {
            if (!(decimal.TryParse(EstimatedCostText.Trim(), NumberStyles.Number, _localization.CurrentCulture, out var parsed) ||
                  decimal.TryParse(EstimatedCostText.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out parsed)) || parsed < 0)
            { ValidationMessage = _localization.GetString("ValidationCostInvalid"); return false; }
            estimatedCost = parsed;
        }
        draft = new MaintenancePlanDraft(SelectedAsset.AssetId, Site.Trim(), Location.Trim(), PlanName.Trim(), Category.Trim(),
            SelectedType.Value, Instructions.Trim(), frequency, SelectedUnit.Value, DateOnly.FromDateTime(NextDueDate.Value),
            AssignedTo.Trim(), estimatedCost, _isActive);
        return true;
    }

    private void ApplySelectedAsset()
    {
        Category = string.IsNullOrWhiteSpace(_selectedAsset.Category) ? "General" : _selectedAsset.Category;
        if (_selectedAsset.AssetId is null) return;
        Site = _selectedAsset.Site;
        Location = _selectedAsset.Location;
    }
}
