using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using GeneralMaintenanceManager.App.Models;
using GeneralMaintenanceManager.App.Services;
using GeneralMaintenanceManager.Core.Models;
using GeneralMaintenanceManager.Core.Entities;

namespace GeneralMaintenanceManager.App.ViewModels;

public sealed class MaintenanceRecordDialogViewModel : ObservableObject
{
    private readonly ILocalizationService _localization;
    private DateTime _maintenanceDate = DateTime.Today;
    private string _site = string.Empty;
    private string _location = string.Empty;
    private string _subject = string.Empty;
    private string _maintenanceType;
    private string _workPerformed = string.Empty;
    private string _performedBy = string.Empty;
    private string _costText = string.Empty;
    private string _notes = string.Empty;
    private string _validationMessage = string.Empty;
    private AssetChoice _selectedAsset;

    public MaintenanceRecordDialogViewModel(MaintenanceEntryOptions options, MaintenanceRecord existing, ILocalizationService localization)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(options);
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        Sites = Normalize(options.Sites);
        Locations = Normalize(options.Locations);
        Subjects = Normalize(options.Subjects);
        AssetTrackingEnabled = options.AssetTrackingEnabled;
        Assets = [new AssetChoice(null, localization.GetString("NoAsset"), string.Empty, string.Empty, "General", string.Empty, string.Empty),
            .. options.Assets.OrderBy(item => item.AssetNumber).ThenBy(item => item.AssetName)
                .Select(item => new AssetChoice(item.AssetId, string.IsNullOrWhiteSpace(item.AssetNumber) ? item.AssetName : $"#{item.AssetNumber} - {item.AssetName}",
                    item.Site, item.Location, item.AssetCategory, item.Condition, item.OperationalStatus))];
        _selectedAsset = Assets[0];
        var maintenanceTypeSource = options.MaintenanceTypes.Count > 0 ? options.MaintenanceTypes : MaintenanceTypeCatalog.BuiltIn;
        MaintenanceTypes = MaintenanceTextPresentation.LocalizeMaintenanceTypes(maintenanceTypeSource, localization);

        DialogTitle = localization.GetString("EditMaintenanceRecord");
        SaveButtonText = localization.GetString("SaveChanges");
        _maintenanceType = MaintenanceTypes[0];
        MaintenanceDate = existing.OccurredAt.LocalDateTime.Date;
        Site = existing.Site;
        Location = existing.Location;
        Subject = existing.Subject;
        MaintenanceType = MaintenanceTextPresentation.LocalizeMaintenanceType(existing.MaintenanceType, localization);
        WorkPerformed = existing.WorkPerformed;
        PerformedBy = existing.PerformedBy;
        CostText = existing.Cost?.ToString("N2", localization.CurrentCulture) ?? string.Empty;
        Notes = existing.Notes;
        if (existing.AssetId.HasValue)
            _selectedAsset = Assets.FirstOrDefault(item => item.AssetId == existing.AssetId.Value) ?? Assets[0];
    }

    public IReadOnlyList<string> Sites { get; }
    public IReadOnlyList<string> Locations { get; }
    public IReadOnlyList<string> Subjects { get; }
    public IReadOnlyList<string> MaintenanceTypes { get; private set; }
    public IReadOnlyList<AssetChoice> Assets { get; }
    public bool AssetTrackingEnabled { get; }
    public AssetChoice SelectedAsset
    {
        get => _selectedAsset;
        set
        {
            if (!SetProperty(ref _selectedAsset, value)) return;
            if (value.AssetId.HasValue)
            {
                if (string.IsNullOrWhiteSpace(Site)) Site = value.Site;
                if (string.IsNullOrWhiteSpace(Location)) Location = value.Location;
            }
        }
    }
    public string DialogTitle { get; }
    public string SaveButtonText { get; }

    public DateTime MaintenanceDate { get => _maintenanceDate; set => SetProperty(ref _maintenanceDate, value); }
    public string Site { get => _site; set => SetProperty(ref _site, value ?? string.Empty); }
    public string Location { get => _location; set => SetProperty(ref _location, value ?? string.Empty); }
    public string Subject { get => _subject; set => SetProperty(ref _subject, value ?? string.Empty); }
    public string MaintenanceType { get => _maintenanceType; set => SetProperty(ref _maintenanceType, value ?? string.Empty); }
    public string WorkPerformed { get => _workPerformed; set => SetProperty(ref _workPerformed, value ?? string.Empty); }
    public string PerformedBy { get => _performedBy; set => SetProperty(ref _performedBy, value ?? string.Empty); }
    public string CostText { get => _costText; set => SetProperty(ref _costText, value ?? string.Empty); }
    public string Notes { get => _notes; set => SetProperty(ref _notes, value ?? string.Empty); }
    public string ValidationMessage { get => _validationMessage; private set => SetProperty(ref _validationMessage, value); }

    private bool TryCreateDraft(out MaintenanceRecordDraft? draft)
    {
        draft = null;
        var location = Location.Trim();
        var subject = Subject.Trim();
        var maintenanceType = MaintenanceTextPresentation.ResolveCanonicalMaintenanceType(MaintenanceType, _localization);
        var workPerformed = WorkPerformed.Trim();
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
        if (maintenanceType.Length == 0)
        {
            ValidationMessage = _localization.GetString("ValidationMaintenanceTypeRequired");
            return false;
        }
        if (workPerformed.Length == 0)
        {
            ValidationMessage = _localization.GetString("ValidationWorkPerformedRequired");
            return false;
        }

        decimal? cost = null;
        if (!string.IsNullOrWhiteSpace(CostText))
        {
            if (!decimal.TryParse(CostText.Trim(), NumberStyles.Number, _localization.CurrentCulture, out var parsedCost)
                && !decimal.TryParse(CostText.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out parsedCost))
            {
                ValidationMessage = _localization.GetString("ValidationCostInvalid");
                return false;
            }
            if (parsedCost < 0)
            {
                ValidationMessage = _localization.GetString("ValidationCostInvalid");
                return false;
            }
            cost = parsedCost;
        }

        var localDate = DateTime.SpecifyKind(MaintenanceDate.Date.Add(DateTime.Now.TimeOfDay), DateTimeKind.Local);
        draft = new MaintenanceRecordDraft(
            new DateTimeOffset(localDate),
            Site.Trim(),
            location,
            subject,
            maintenanceType,
            workPerformed,
            PerformedBy.Trim(),
            cost,
            Notes.Trim(),
            AssetTrackingEnabled ? SelectedAsset.AssetId : null);
        ValidationMessage = string.Empty;
        return true;
    }

    public bool TryCreateEditDraft(out MaintenanceRecordEditDraft? draft)
    {
        draft = null;
        if (!TryCreateDraft(out var createDraft) || createDraft is null) return false;
        draft = new MaintenanceRecordEditDraft(
            createDraft.OccurredAt, createDraft.Site, createDraft.Location, createDraft.Subject,
            createDraft.MaintenanceType, createDraft.WorkPerformed, createDraft.PerformedBy, createDraft.Cost, createDraft.Notes);
        return true;
    }

    private static string[] Normalize(IEnumerable<string> source) => source
        .Select(item => item?.Trim() ?? string.Empty)
        .Where(item => item.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(item => item, StringComparer.CurrentCultureIgnoreCase)
        .ToArray();
}
