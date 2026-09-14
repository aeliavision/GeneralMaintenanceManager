using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using GeneralMaintenanceManager.App.Models;
using GeneralMaintenanceManager.App.Services;
using GeneralMaintenanceManager.Core.Entities;
using GeneralMaintenanceManager.Core.Models;

namespace GeneralMaintenanceManager.App.ViewModels;

public sealed class AssetDialogViewModel : ObservableObject
{
    private readonly ILocalizationService _localization;
    private string _assetNumber;
    private string _hospital;
    private string _department;
    private string _machineName;
    private string _assetCategory;
    private string _company;
    private string _country;
    private string _model;
    private string _serialNumber;
    private string _condition;
    private string _operationalStatus;
    private string _voltage;
    private string _priceText;
    private string _notes;
    private string _validationMessage = string.Empty;

    public AssetDialogViewModel(Asset? asset, ILocalizationService localization)
    {
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));

        IsNew = asset is null;
        IsAssetNumberEditable = asset is null;
        Title = IsNew
            ? _localization.GetString("AddAssetDialogTitle")
            : _localization.Format("EditAssetDialogTitleFormat", string.IsNullOrWhiteSpace(asset!.AssetNumber) ? _localization.GetString("UnassignedAsset") : asset.AssetNumber);

        _assetNumber = asset?.AssetNumber ?? string.Empty;
        _hospital = asset?.Site ?? string.Empty;
        _department = asset?.Location ?? string.Empty;
        _machineName = asset?.AssetName ?? string.Empty;
        _assetCategory = string.IsNullOrWhiteSpace(asset?.AssetCategory) ? "General" : asset!.AssetCategory.Trim();
        _company = asset?.Manufacturer ?? string.Empty;
        _country = asset?.Country ?? string.Empty;
        _model = asset?.Model ?? string.Empty;
        _serialNumber = asset?.SerialNumber ?? string.Empty;
        _condition = asset?.Condition ?? "Good";
        _operationalStatus = asset?.OperationalStatus ?? "In Service";
        _voltage = asset?.TechnicalSpecification ?? string.Empty;
        _priceText = asset?.PurchasePrice?.ToString("0.##", _localization.CurrentCulture) ?? string.Empty;
        _notes = asset?.Notes ?? string.Empty;

        Conditions = ConditionOptionFactory.Create(_localization, _condition);
        AssetCategories = AssetClassificationOptionFactory.CreateCategories(_localization, _assetCategory);
        OperationalStatuses = AssetClassificationOptionFactory.CreateOperationalStatuses(_localization, _operationalStatus);
    }

    public bool IsNew { get; }

    public bool IsAssetNumberEditable { get; }

    public string Title { get; }

    public IReadOnlyList<ConditionOption> Conditions { get; }

    public IReadOnlyList<ChoiceOption<string>> AssetCategories { get; }

    public IReadOnlyList<ChoiceOption<string>> OperationalStatuses { get; }

    public string AssetNumber
    {
        get => _assetNumber;
        set => SetProperty(ref _assetNumber, value ?? string.Empty);
    }

    public string Site
    {
        get => _hospital;
        set => SetProperty(ref _hospital, value ?? string.Empty);
    }

    public string Location
    {
        get => _department;
        set => SetProperty(ref _department, value ?? string.Empty);
    }

    public string AssetName
    {
        get => _machineName;
        set => SetProperty(ref _machineName, value ?? string.Empty);
    }


    public string AssetCategory
    {
        get => _assetCategory;
        set => SetProperty(ref _assetCategory, value ?? string.Empty);
    }

    public string Manufacturer
    {
        get => _company;
        set => SetProperty(ref _company, value ?? string.Empty);
    }

    public string Country
    {
        get => _country;
        set => SetProperty(ref _country, value ?? string.Empty);
    }

    public string Model
    {
        get => _model;
        set => SetProperty(ref _model, value ?? string.Empty);
    }

    public string SerialNumber
    {
        get => _serialNumber;
        set => SetProperty(ref _serialNumber, value ?? string.Empty);
    }

    public string Condition
    {
        get => _condition;
        set => SetProperty(ref _condition, value ?? string.Empty);
    }


    public string OperationalStatus
    {
        get => _operationalStatus;
        set => SetProperty(ref _operationalStatus, value ?? string.Empty);
    }

    public string TechnicalSpecification
    {
        get => _voltage;
        set => SetProperty(ref _voltage, value ?? string.Empty);
    }

    public string PriceText
    {
        get => _priceText;
        set => SetProperty(ref _priceText, value ?? string.Empty);
    }

    public string Notes
    {
        get => _notes;
        set => SetProperty(ref _notes, value ?? string.Empty);
    }

    public string ValidationMessage
    {
        get => _validationMessage;
        private set => SetProperty(ref _validationMessage, value);
    }

    public bool TryCreateDraft(out AssetDraft? draft)
    {
        draft = null;
        ValidationMessage = string.Empty;

        if (string.IsNullOrWhiteSpace(AssetName))
        {
            ValidationMessage = _localization.GetString("ValidationAssetNameRequired");
            return false;
        }

        decimal? price = null;
        if (!string.IsNullOrWhiteSpace(PriceText))
        {
            if (!decimal.TryParse(
                    PriceText.Trim(),
                    NumberStyles.Number | NumberStyles.AllowCurrencySymbol,
                    _localization.CurrentCulture,
                    out var parsed)
                && !decimal.TryParse(
                    PriceText.Trim(),
                    NumberStyles.Number | NumberStyles.AllowCurrencySymbol,
                    CultureInfo.InvariantCulture,
                    out parsed))
            {
                ValidationMessage = _localization.GetString("ValidationPriceInvalid");
                return false;
            }

            if (parsed < 0)
            {
                ValidationMessage = _localization.GetString("ValidationPriceNegative");
                return false;
            }

            price = parsed;
        }

        draft = new AssetDraft(
            AssetNumber.Trim(),
            Site.Trim(),
            Location.Trim(),
            AssetName.Trim(),
            AssetCategory.Trim(),
            Manufacturer.Trim(),
            Country.Trim(),
            Model.Trim(),
            SerialNumber.Trim(),
            Condition.Trim(),
            OperationalStatus.Trim(),
            TechnicalSpecification.Trim(),
            PurchaseDate: null,
            PurchasePrice: price,
            InstallationDate: null,
            WarrantyExpiryDate: null,
            Notes: Notes.Trim());

        return true;
    }
}
