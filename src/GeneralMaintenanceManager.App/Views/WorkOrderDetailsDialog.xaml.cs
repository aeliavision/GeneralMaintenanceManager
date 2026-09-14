using System.Windows;
using GeneralMaintenanceManager.App.Models;
using GeneralMaintenanceManager.App.Services;
using GeneralMaintenanceManager.App.ViewModels;
using GeneralMaintenanceManager.Core.Entities;

namespace GeneralMaintenanceManager.App.Views;

public partial class WorkOrderDetailsDialog : Window
{
    public WorkOrderDetailsAction Action { get; private set; } = WorkOrderDetailsAction.Close;
    private readonly IPrintService _printService;
    private readonly WorkOrder _workOrder;
    private readonly Asset? _asset;
    private readonly ILocalizationService _localization;

    public WorkOrderDetailsDialog(
        WorkOrderDetailsDialogViewModel viewModel,
        IPrintService printService,
        WorkOrder workOrder,
        Asset? asset,
        ILocalizationService localization)
    {
        InitializeComponent();
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _printService = printService ?? throw new ArgumentNullException(nameof(printService));
        _workOrder = workOrder ?? throw new ArgumentNullException(nameof(workOrder));
        _asset = asset;
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        NativeWindowAppearance.Attach(this);
        ResponsiveWindowSizing.Attach(this);
    }

    private void Print_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _printService.PrintWorkOrder(_workOrder, _asset);
        }
        catch (Exception ex)
        {
            ModernMessageDialog.ShowError(this, _localization.GetString("ErrorPrintFailedTitle"), ex.Message, _localization.CurrentCulture);
        }
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        Action = WorkOrderDetailsAction.Edit;
        Close();
    }

    private void OpenMaintenance_Click(object sender, RoutedEventArgs e)
    {
        Action = WorkOrderDetailsAction.OpenMaintenanceRecord;
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
