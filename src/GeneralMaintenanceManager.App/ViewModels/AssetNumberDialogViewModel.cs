using CommunityToolkit.Mvvm.ComponentModel;
using GeneralMaintenanceManager.App.Services;
using GeneralMaintenanceManager.Core.Entities;
using GeneralMaintenanceManager.Core.Models;

namespace GeneralMaintenanceManager.App.ViewModels;

public sealed class AssetNumberDialogViewModel : ObservableObject
{
    private readonly ILocalizationService _localization;
    private string _assetNumber;
    private string _reason = string.Empty;
    private string _validationMessage = string.Empty;

    public AssetNumberDialogViewModel(Asset asset, ILocalizationService localization)
    {
        ArgumentNullException.ThrowIfNull(asset);
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _assetNumber = string.IsNullOrWhiteSpace(asset.AssetNumber)
            ? asset.ImportedAssetNumber.Trim()
            : asset.AssetNumber;
        AssetName = asset.AssetName;
        ImportedAssetNumber = asset.ImportedAssetNumber;
        IsCorrection = !string.IsNullOrWhiteSpace(asset.AssetNumber);
    }

    public string AssetName { get; }
    public string ImportedAssetNumber { get; }
    public bool IsCorrection { get; }

    public string AssetNumber
    {
        get => _assetNumber;
        set => SetProperty(ref _assetNumber, value ?? string.Empty);
    }

    public string Reason
    {
        get => _reason;
        set => SetProperty(ref _reason, value ?? string.Empty);
    }

    public string ValidationMessage
    {
        get => _validationMessage;
        private set => SetProperty(ref _validationMessage, value);
    }

    public bool TryCreateDraft(out AssetNumberChangeDraft? draft)
    {
        draft = null;
        ValidationMessage = string.Empty;
        if (string.IsNullOrWhiteSpace(AssetNumber))
        {
            ValidationMessage = _localization.GetString("ValidationAssetRequired");
            return false;
        }
        if (string.IsNullOrWhiteSpace(Reason))
        {
            ValidationMessage = _localization.GetString("ValidationReviewReasonRequired");
            return false;
        }
        draft = new AssetNumberChangeDraft(AssetNumber.Trim(), Reason.Trim());
        return true;
    }
}
