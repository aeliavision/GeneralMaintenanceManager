using System.Globalization;
using System.Windows;
using System.Windows.Data;
using GeneralMaintenanceManager.Core.Enums;

namespace GeneralMaintenanceManager.App.Converters;

public sealed class MaintenanceEnumTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value switch
        {
            WorkOrderStatus.New => "WorkOrderStatusNew",
            WorkOrderStatus.Assigned => "WorkOrderStatusAssigned",
            WorkOrderStatus.InProgress => "WorkOrderStatusInProgress",
            WorkOrderStatus.OnHold => "WorkOrderStatusOnHold",
            WorkOrderStatus.Completed => "WorkOrderStatusCompleted",
            WorkOrderStatus.Cancelled => "WorkOrderStatusCancelled",
            WorkOrderPriority.Low => "PriorityLow",
            WorkOrderPriority.Normal => "PriorityNormal",
            WorkOrderPriority.High => "PriorityHigh",
            WorkOrderPriority.Urgent => "PriorityUrgent",
            MaintenanceType.CorrectiveMaintenance => "MaintenanceTypeCorrective",
            MaintenanceType.PreventiveMaintenance => "MaintenanceTypePreventive",
            MaintenanceType.Inspection => "MaintenanceTypeInspection",
            MaintenanceType.RoutineService => "MaintenanceTypeRoutineService",
            MaintenanceType.Breakdown => "MaintenanceTypeBreakdown",
            MaintenanceType.Installation => "MaintenanceTypeInstallation",
            MaintenanceType.Relocation => "MaintenanceTypeRelocation",
            MaintenanceType.Replacement => "MaintenanceTypeReplacement",
            MaintenanceType.TestOrCalibration => "MaintenanceTypeCalibration",
            MaintenanceType.GeneralMaintenance => "MaintenanceTypeGeneral",
            _ => string.Empty
        };

        if (key.Length == 0) return value?.ToString() ?? string.Empty;
        return Application.Current?.TryFindResource(key)?.ToString() ?? value?.ToString() ?? string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
