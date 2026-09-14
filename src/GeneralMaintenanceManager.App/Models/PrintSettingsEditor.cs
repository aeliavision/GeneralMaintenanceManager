using GeneralMaintenanceManager.App.Services;

namespace GeneralMaintenanceManager.App.Models;

public sealed class PrintSettingsEditor
{
    public bool AutoPrintWorkOrderActions { get; set; }
    public string HeaderText { get; set; } = string.Empty;
    public string FooterText { get; set; } = string.Empty;
    public string CenterBrandingMode { get; set; } = PrintCenterBrandingMode.Logo.ToString();
    public string CenterText { get; set; } = string.Empty;
    public string? LogoPath { get; set; }
    public WorkOrderPrintLayoutEditor WorkOrder { get; set; } = new();
    public MaintenancePrintLayoutEditor Maintenance { get; set; } = new();
    public ListReportPrintLayoutEditor ListReport { get; set; } = new();

    public static PrintSettingsEditor FromSettings(PrintSettings settings) => new()
    {
        AutoPrintWorkOrderActions = settings.AutoPrintWorkOrderActions,
        HeaderText = settings.HeaderText,
        FooterText = settings.FooterText,
        CenterBrandingMode = settings.CenterBrandingMode.ToString(),
        CenterText = settings.CenterText,
        LogoPath = settings.LogoPath,
        WorkOrder = WorkOrderPrintLayoutEditor.From(settings.WorkOrderLayout),
        Maintenance = MaintenancePrintLayoutEditor.From(settings.MaintenanceLayout),
        ListReport = ListReportPrintLayoutEditor.From(settings.ListReportLayout)
    };

    public PrintSettings ToSettings()
    {
        var brandingMode = Enum.TryParse<PrintCenterBrandingMode>(CenterBrandingMode, true, out var mode)
            ? mode
            : PrintCenterBrandingMode.Logo;
        return new PrintSettings(
            AutoPrintWorkOrderActions,
            HeaderText ?? string.Empty,
            FooterText ?? string.Empty,
            brandingMode,
            CenterText ?? string.Empty,
            LogoPath,
            WorkOrder.ToLayout(),
            Maintenance.ToLayout(),
            ListReport.ToLayout());
    }
}

public sealed class WorkOrderPrintLayoutEditor
{
    public bool ShowOrderNumber { get; set; } = true;
    public bool ShowAsset { get; set; } = true;
    public bool ShowPriority { get; set; } = true;
    public bool ShowStatus { get; set; } = true;
    public bool ShowLocation { get; set; } = true;
    public bool ShowProblem { get; set; } = true;
    public bool ShowWorkPerformed { get; set; } = true;
    public bool ShowPerformedBy { get; set; } = true;
    public bool ShowCost { get; set; } = true;
    public bool ShowNotes { get; set; } = true;
    public bool ShowPrintDate { get; set; } = true;
    public bool ShowSignature { get; set; } = true;

    public static WorkOrderPrintLayoutEditor From(WorkOrderPrintLayout value) => new()
    {
        ShowOrderNumber = value.ShowOrderNumber,
        ShowAsset = value.ShowAsset,
        ShowPriority = value.ShowPriority,
        ShowStatus = value.ShowStatus,
        ShowLocation = value.ShowLocation,
        ShowProblem = value.ShowProblem,
        ShowWorkPerformed = value.ShowWorkPerformed,
        ShowPerformedBy = value.ShowPerformedBy,
        ShowCost = value.ShowCost,
        ShowNotes = value.ShowNotes,
        ShowPrintDate = value.ShowPrintDate,
        ShowSignature = value.ShowSignature
    };

    public WorkOrderPrintLayout ToLayout() => new(
        ShowOrderNumber, ShowAsset, ShowPriority, ShowStatus, ShowLocation, ShowProblem,
        ShowWorkPerformed, ShowPerformedBy, ShowCost, ShowNotes, ShowPrintDate, ShowSignature);
}

public sealed class MaintenancePrintLayoutEditor
{
    public bool ShowReference { get; set; } = true;
    public bool ShowDate { get; set; } = true;
    public bool ShowMaintenanceType { get; set; } = true;
    public bool ShowAsset { get; set; } = true;
    public bool ShowLocation { get; set; } = true;
    public bool ShowProblem { get; set; } = true;
    public bool ShowWorkPerformed { get; set; } = true;
    public bool ShowPerformedBy { get; set; } = true;
    public bool ShowCost { get; set; } = true;
    public bool ShowNotes { get; set; } = true;
    public bool ShowAuditInformation { get; set; } = true;
    public bool ShowPrintDate { get; set; } = true;
    public bool ShowSignature { get; set; } = true;

    public static MaintenancePrintLayoutEditor From(MaintenancePrintLayout value) => new()
    {
        ShowReference = value.ShowReference,
        ShowDate = value.ShowDate,
        ShowMaintenanceType = value.ShowMaintenanceType,
        ShowAsset = value.ShowAsset,
        ShowLocation = value.ShowLocation,
        ShowProblem = value.ShowProblem,
        ShowWorkPerformed = value.ShowWorkPerformed,
        ShowPerformedBy = value.ShowPerformedBy,
        ShowCost = value.ShowCost,
        ShowNotes = value.ShowNotes,
        ShowAuditInformation = value.ShowAuditInformation,
        ShowPrintDate = value.ShowPrintDate,
        ShowSignature = value.ShowSignature
    };

    public MaintenancePrintLayout ToLayout() => new(
        ShowReference, ShowDate, ShowMaintenanceType, ShowAsset, ShowLocation, ShowProblem, ShowWorkPerformed,
        ShowPerformedBy, ShowCost, ShowNotes, ShowAuditInformation, ShowPrintDate, ShowSignature);
}

public sealed class ListReportPrintLayoutEditor
{
    public bool ShowReference { get; set; } = true;
    public bool ShowDate { get; set; } = true;
    public bool ShowType { get; set; } = true;
    public bool ShowLocation { get; set; } = true;
    public bool ShowPerformedBy { get; set; } = true;
    public bool ShowCost { get; set; } = true;
    public bool ShowNotes { get; set; } = true;
    public bool ShowPrintDate { get; set; } = true;

    public static ListReportPrintLayoutEditor From(ListReportPrintLayout value) => new()
    {
        ShowReference = value.ShowReference,
        ShowDate = value.ShowDate,
        ShowType = value.ShowType,
        ShowLocation = value.ShowLocation,
        ShowPerformedBy = value.ShowPerformedBy,
        ShowCost = value.ShowCost,
        ShowNotes = value.ShowNotes,
        ShowPrintDate = value.ShowPrintDate
    };

    public ListReportPrintLayout ToLayout() => new(
        ShowReference, ShowDate, ShowType, ShowLocation, ShowPerformedBy, ShowCost, ShowNotes, ShowPrintDate);
}
