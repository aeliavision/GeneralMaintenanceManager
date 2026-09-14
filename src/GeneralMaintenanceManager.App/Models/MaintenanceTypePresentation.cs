using GeneralMaintenanceManager.App.Services;
using GeneralMaintenanceManager.Core.Enums;

namespace GeneralMaintenanceManager.App.Models;

public static class MaintenanceTypePresentation
{
    public static string GetDisplayText(MaintenanceType maintenanceType, ILocalizationService localization)
    {
        ArgumentNullException.ThrowIfNull(localization);

        return maintenanceType switch
        {
            MaintenanceType.CorrectiveMaintenance => localization.GetString("MaintenanceTypeCorrective"),
            MaintenanceType.PreventiveMaintenance => localization.GetString("MaintenanceTypePreventive"),
            MaintenanceType.Inspection => localization.GetString("MaintenanceTypeInspection"),
            MaintenanceType.RoutineService => localization.GetString("MaintenanceTypeRoutineService"),
            MaintenanceType.Breakdown => localization.GetString("MaintenanceTypeBreakdown"),
            MaintenanceType.Installation => localization.GetString("MaintenanceTypeInstallation"),
            MaintenanceType.Relocation => localization.GetString("MaintenanceTypeRelocation"),
            MaintenanceType.Replacement => localization.GetString("MaintenanceTypeReplacement"),
            MaintenanceType.TestOrCalibration => localization.GetString("MaintenanceTypeCalibration"),
            MaintenanceType.GeneralMaintenance => localization.GetString("MaintenanceTypeGeneral"),
            MaintenanceType.AssetAdded => localization.GetString("HistoryTypeAdded"),
            MaintenanceType.AssetImported => localization.GetString("HistoryTypeImported"),
            MaintenanceType.AssetInformationUpdated => localization.GetString("HistoryTypeUpdated"),
            MaintenanceType.Note => localization.GetString("HistoryTypeNote"),
            _ => maintenanceType.ToString()
        };
    }

    public static ChoiceOption<MaintenanceType>[] CreateMaintenanceTypeChoices(ILocalizationService localization)
    {
        ArgumentNullException.ThrowIfNull(localization);

        return
        [
            new(MaintenanceType.CorrectiveMaintenance, GetDisplayText(MaintenanceType.CorrectiveMaintenance, localization)),
            new(MaintenanceType.PreventiveMaintenance, GetDisplayText(MaintenanceType.PreventiveMaintenance, localization)),
            new(MaintenanceType.Inspection, GetDisplayText(MaintenanceType.Inspection, localization)),
            new(MaintenanceType.RoutineService, GetDisplayText(MaintenanceType.RoutineService, localization)),
            new(MaintenanceType.Breakdown, GetDisplayText(MaintenanceType.Breakdown, localization)),
            new(MaintenanceType.Installation, GetDisplayText(MaintenanceType.Installation, localization)),
            new(MaintenanceType.Relocation, GetDisplayText(MaintenanceType.Relocation, localization)),
            new(MaintenanceType.Replacement, GetDisplayText(MaintenanceType.Replacement, localization)),
            new(MaintenanceType.TestOrCalibration, GetDisplayText(MaintenanceType.TestOrCalibration, localization)),
            new(MaintenanceType.GeneralMaintenance, GetDisplayText(MaintenanceType.GeneralMaintenance, localization))
        ];
    }
}
