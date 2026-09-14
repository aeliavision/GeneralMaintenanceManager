using GeneralMaintenanceManager.Core.Enums;

namespace GeneralMaintenanceManager.Core.Models;

public static class MaintenanceTypeCatalog
{
    public static IReadOnlyList<string> BuiltIn { get; } =
    [
        "General Maintenance",
        "Repair",
        "HVAC",
        "Electrical",
        "Plumbing",
        "Service",
        "Inspection",
        "Cleaning",
        "Installation",
        "Replacement",
        "Test / Calibration",
        "Other"
    ];

}
