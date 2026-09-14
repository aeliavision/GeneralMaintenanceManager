using GeneralMaintenanceManager.Core.Enums;
using GeneralMaintenanceManager.Core.Entities;
using GeneralMaintenanceManager.Core.Models;

namespace GeneralMaintenanceManager.App.Services;

public static class MaintenanceTextPresentation
{
    public static string LocalizeMaintenanceType(string? value, ILocalizationService localization)
    {
        ArgumentNullException.ThrowIfNull(localization);
        var normalized = value?.Trim() ?? string.Empty;
        return normalized switch
        {
            "General Maintenance" => localization.GetString("MaintenanceTypeGeneral"),
            "Repair" => localization.GetString("MaintenanceTypeCorrective"),
            "HVAC" => localization.GetString("MaintenanceTypeHVAC"),
            "Electrical" => localization.GetString("MaintenanceTypeElectrical"),
            "Plumbing" => localization.GetString("MaintenanceTypePlumbing"),
            "Service" => localization.GetString("MaintenanceTypeService"),
            "Inspection" => localization.GetString("MaintenanceTypeInspection"),
            "Cleaning" => localization.GetString("MaintenanceTypeCleaning"),
            "Installation" => localization.GetString("MaintenanceTypeInstallation"),
            "Replacement" => localization.GetString("MaintenanceTypeReplacement"),
            "Test / Calibration" => localization.GetString("MaintenanceTypeCalibration"),
            "Other" => localization.GetString("MaintenanceTypeOther"),
            _ => normalized
        };
    }

    public static string ResolveCanonicalMaintenanceType(string? value, ILocalizationService localization)
    {
        ArgumentNullException.ThrowIfNull(localization);
        var normalized = value?.Trim() ?? string.Empty;
        foreach (var canonical in MaintenanceTypeCatalog.BuiltIn)
        {
            if (string.Equals(normalized, canonical, StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, LocalizeMaintenanceType(canonical, localization), StringComparison.OrdinalIgnoreCase))
            {
                return canonical;
            }
        }

        return normalized;
    }

    public static IReadOnlyList<string> LocalizeMaintenanceTypes(IEnumerable<string> values, ILocalizationService localization)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(localization);
        var seen = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        var result = new List<string>();
        foreach (var value in values)
        {
            var display = LocalizeMaintenanceType(value, localization);
            if (display.Length > 0 && seen.Add(display)) result.Add(display);
        }
        return result;
    }

    public static string LocalizeWorkOrderStatus(WorkOrderStatus status, ILocalizationService localization) => status switch
    {
        WorkOrderStatus.New => localization.GetString("WorkOrderStatusNew"),
        WorkOrderStatus.Assigned => localization.GetString("WorkOrderStatusAssigned"),
        WorkOrderStatus.InProgress => localization.GetString("WorkOrderStatusInProgress"),
        WorkOrderStatus.OnHold => localization.GetString("WorkOrderStatusOnHold"),
        WorkOrderStatus.Completed => localization.GetString("WorkOrderStatusCompleted"),
        WorkOrderStatus.Cancelled => localization.GetString("WorkOrderStatusCancelled"),
        _ => status.ToString()
    };

    public static string LocalizeFrequencyUnit(MaintenanceFrequencyUnit unit, ILocalizationService localization) => unit switch
    {
        MaintenanceFrequencyUnit.Days => localization.GetString("FrequencyDays"),
        MaintenanceFrequencyUnit.Weeks => localization.GetString("FrequencyWeeks"),
        MaintenanceFrequencyUnit.Months => localization.GetString("FrequencyMonths"),
        MaintenanceFrequencyUnit.Years => localization.GetString("FrequencyYears"),
        _ => unit.ToString()
    };
    public static string LocalizeWorkOrderPriority(WorkOrderPriority priority, ILocalizationService localization) => priority switch
    {
        WorkOrderPriority.Low => localization.GetString("PriorityLow"),
        WorkOrderPriority.High => localization.GetString("PriorityHigh"),
        WorkOrderPriority.Urgent => localization.GetString("PriorityUrgent"),
        _ => localization.GetString("PriorityNormal")
    };

    public static string LocalizeActivityType(string? type, ILocalizationService localization) => (type ?? string.Empty).Trim() switch
    {
        ActivityTypes.MaintenanceCreated => localization.GetString("ActivityMaintenanceCreated"),
        ActivityTypes.MaintenanceEdited => localization.GetString("ActivityMaintenanceEdited"),
        ActivityTypes.WorkOrderCreated => localization.GetString("ActivityWorkOrderCreated"),
        ActivityTypes.WorkOrderAssigned => localization.GetString("ActivityWorkOrderAssigned"),
        ActivityTypes.WorkOrderStarted => localization.GetString("ActivityWorkOrderStarted"),
        ActivityTypes.WorkOrderResumed => localization.GetString("ActivityWorkOrderResumed"),
        ActivityTypes.WorkOrderPutOnHold => localization.GetString("ActivityWorkOrderPutOnHold"),
        ActivityTypes.WorkOrderCompleted => localization.GetString("ActivityWorkOrderCompleted"),
        ActivityTypes.WorkOrderCompletionRecovered => localization.GetString("ActivityWorkOrderCompletionRecovered"),
        ActivityTypes.WorkOrderCancelled => localization.GetString("ActivityWorkOrderCancelled"),
        ActivityTypes.WorkOrderEdited => localization.GetString("ActivityWorkOrderEdited"),
        ActivityTypes.PreventivePlanCreated => localization.GetString("ActivityPreventivePlanCreated"),
        ActivityTypes.PreventivePlanEdited => localization.GetString("ActivityPreventivePlanEdited"),
        ActivityTypes.PreventivePlanActivated => localization.GetString("ActivityPreventivePlanActivated"),
        ActivityTypes.PreventivePlanPaused => localization.GetString("ActivityPreventivePlanPaused"),
        ActivityTypes.PreventivePlanDue => localization.GetString("ActivityPreventivePlanDue"),
        ActivityTypes.PreventivePlanGeneratedWorkOrder => localization.GetString("ActivityPreventivePlanGeneratedWorkOrder"),
        ActivityTypes.PreventivePlanCompletedOccurrence => localization.GetString("ActivityPreventivePlanCompletedOccurrence"),
        var value => value
    };

    public static string LocalizeMaintenanceActivityType(string? type, ILocalizationService localization) => (type ?? string.Empty).Trim() switch
    {
        MaintenanceActivityTypes.Created => localization.GetString("ActivityMaintenanceCreated"),
        MaintenanceActivityTypes.Edited => localization.GetString("ActivityMaintenanceEdited"),
        MaintenanceActivityTypes.MarkedInvalid => localization.GetString("MaintenanceMarkedInvalid"),
        var value => value
    };


    public static string FormatMaintenanceActivityDescription(MaintenanceActivity activity, ILocalizationService localization)
    {
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(localization);

        var reason = activity.Reason?.Trim() ?? string.Empty;
        var changes = activity.Changes?.Trim() ?? string.Empty;

        if (string.Equals(activity.ActivityType, MaintenanceActivityTypes.MarkedInvalid, StringComparison.Ordinal))
            return reason.Length > 0 ? reason : localization.GetString("MaintenanceMarkedInvalid");

        if (string.Equals(activity.ActivityType, MaintenanceActivityTypes.Created, StringComparison.Ordinal))
        {
            var fields = ParseSimpleAuditFields(changes);
            var parts = new List<string>();

            if (fields.TryGetValue("Type", out var type) && type.Length > 0)
                parts.Add($"{localization.GetString("MaintenanceType")}: {LocalizeMaintenanceType(type, localization)}");
            if (fields.TryGetValue("Location", out var location) && location.Length > 0)
                parts.Add($"{localization.GetString("Location")}: {location}");
            if (fields.TryGetValue("SourceWorkOrder", out var workOrder) && workOrder.Length > 0)
                parts.Add($"{localization.GetString("WorkOrderNumber")}: {workOrder}");

            if (parts.Count > 0)
                return string.Join("  •  ", parts);
        }

        if (string.Equals(activity.ActivityType, MaintenanceActivityTypes.Edited, StringComparison.Ordinal) && changes.Length > 0)
            return HumanizeMaintenanceChanges(changes, localization);

        if (reason.Length > 0 && changes.Length > 0) return $"{reason} — {changes}";
        return reason.Length > 0 ? reason : changes;
    }

    private static Dictionary<string, string> ParseSimpleAuditFields(string changes)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var segment in changes.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = segment.IndexOf('=');
            if (separator <= 0) continue;
            var key = segment[..separator].Trim();
            var value = segment[(separator + 1)..].Trim();
            if (key.Length > 0) result[key] = value;
        }
        return result;
    }

    private static string HumanizeMaintenanceChanges(string changes, ILocalizationService localization)
    {
        var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Date"] = localization.GetString("Date"),
            ["Site"] = localization.GetString("Site"),
            ["Location"] = localization.GetString("Location"),
            ["Subject"] = localization.GetString("Subject"),
            ["MaintenanceType"] = localization.GetString("MaintenanceType"),
            ["WorkPerformed"] = localization.GetString("WorkPerformedDone"),
            ["PerformedBy"] = localization.GetString("PerformedBy"),
            ["Cost"] = localization.GetString("Cost"),
            ["Notes"] = localization.GetString("Notes")
        };

        var parts = new List<string>();
        foreach (var rawPart in changes.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = rawPart.IndexOf(':');
            if (separator <= 0)
            {
                parts.Add(rawPart);
                continue;
            }

            var key = rawPart[..separator].Trim();
            var value = rawPart[(separator + 1)..].Trim();
            parts.Add($"{(labels.TryGetValue(key, out var label) ? label : key)}: {value}");
        }
        return string.Join("  •  ", parts);
    }
}
