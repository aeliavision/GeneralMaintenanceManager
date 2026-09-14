using GeneralMaintenanceManager.App.Services;
using GeneralMaintenanceManager.Core.Entities;
using GeneralMaintenanceManager.Core.Models;

namespace GeneralMaintenanceManager.App.ViewModels;

public sealed class ReviewCaseViewModel
{
    public ReviewCaseViewModel(ReviewCase source, ILocalizationService localization)
    {
        Source = source;
        ReviewId = source.ReviewId;
        ReviewType = source.ReviewType;
        Reasons = LocalizeReasons(source.Reasons, localization);
        AssetA = source.AssetA;
        AssetB = source.AssetB;
        TypeLabel = source.ReviewType switch
        {
            "PossibleDuplicate" => localization.GetString("ReviewPossibleDuplicate"),
            "AssetConflict" => localization.GetString("ReviewAssetConflict"),
            "MissingAsset" => localization.GetString("ReviewMissingAsset"),
            _ => source.ReviewType
        };
        AssetADisplay = DisplayAsset(AssetA.AssetNumber, localization);
        AssetBDisplay = AssetB is null ? string.Empty : DisplayAsset(AssetB.AssetNumber, localization);
    }

    public ReviewCase Source { get; }
    public Guid ReviewId { get; }
    public string ReviewType { get; }
    public string TypeLabel { get; }
    public string Reasons { get; }
    public Asset AssetA { get; }
    public Asset? AssetB { get; }
    public bool HasSecondRecord => AssetB is not null;
    public string AssetADisplay { get; }
    public string AssetBDisplay { get; }


    private static string LocalizeReasons(string raw, ILocalizationService localization)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }
        var lines = new List<string>();
        foreach (var line in raw.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = line.IndexOf('=');
            var code = separator < 0 ? line : line[..separator];
            var value = separator < 0 ? string.Empty : line[(separator + 1)..];
            lines.Add(code switch
            {
                "MissingAsset" => localization.GetString("ReviewReasonMissingAsset"),
                "AssetConflict" => localization.Format("ReviewReasonAssetConflictFormat", value),
                "ExactSerial" => localization.Format("ReviewReasonExactSerialFormat", value),
                "SameMachine" => localization.Format("ReviewReasonSameMachineFormat", value),
                "SameManufacturer" => localization.Format("ReviewReasonSameManufacturerFormat", value),
                "SameModel" => localization.Format("ReviewReasonSameModelFormat", value),
                "SameLocation" => localization.Format("ReviewReasonSameLocationFormat", value),
                _ => line
            });
        }
        return string.Join(Environment.NewLine, lines);
    }

    private static string DisplayAsset(string value, ILocalizationService localization) =>
        string.IsNullOrWhiteSpace(value) ? localization.GetString("UnassignedAsset") : $"#{value}";
}
