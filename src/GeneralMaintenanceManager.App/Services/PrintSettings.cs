namespace GeneralMaintenanceManager.App.Services;

public enum PrintCenterBrandingMode
{
    Logo,
    Text
}

public sealed record WorkOrderPrintLayout(
    bool ShowOrderNumber,
    bool ShowAsset,
    bool ShowPriority,
    bool ShowStatus,
    bool ShowLocation,
    bool ShowProblem,
    bool ShowWorkPerformed,
    bool ShowPerformedBy,
    bool ShowCost,
    bool ShowNotes,
    bool ShowPrintDate,
    bool ShowSignature)
{
    public static WorkOrderPrintLayout Default { get; } = new(
        true, true, true, true, true, true, true, true, true, true, true, true);
}

public sealed record MaintenancePrintLayout(
    bool ShowReference,
    bool ShowDate,
    bool ShowMaintenanceType,
    bool ShowAsset,
    bool ShowLocation,
    bool ShowProblem,
    bool ShowWorkPerformed,
    bool ShowPerformedBy,
    bool ShowCost,
    bool ShowNotes,
    bool ShowAuditInformation,
    bool ShowPrintDate,
    bool ShowSignature)
{
    public static MaintenancePrintLayout Default { get; } = new(
        true, true, true, true, true, true, true, true, true, true, true, true, true);
}

public sealed record ListReportPrintLayout(
    bool ShowReference,
    bool ShowDate,
    bool ShowType,
    bool ShowLocation,
    bool ShowPerformedBy,
    bool ShowCost,
    bool ShowNotes,
    bool ShowPrintDate)
{
    public static ListReportPrintLayout Default { get; } = new(
        true, true, true, true, true, true, true, true);
}

public sealed record PrintSettings(
    bool AutoPrintWorkOrderActions,
    string HeaderText,
    string FooterText,
    PrintCenterBrandingMode CenterBrandingMode,
    string CenterText,
    string? LogoPath,
    WorkOrderPrintLayout WorkOrderLayout,
    MaintenancePrintLayout MaintenanceLayout,
    ListReportPrintLayout ListReportLayout)
{
    public static PrintSettings Default { get; } = new(
        false,
        string.Empty,
        string.Empty,
        PrintCenterBrandingMode.Logo,
        string.Empty,
        null,
        WorkOrderPrintLayout.Default,
        MaintenancePrintLayout.Default,
        ListReportPrintLayout.Default);
}
