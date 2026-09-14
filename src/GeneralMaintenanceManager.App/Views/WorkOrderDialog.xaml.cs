using System.Windows;
using GeneralMaintenanceManager.App.ViewModels;
using GeneralMaintenanceManager.Core.Models;

namespace GeneralMaintenanceManager.App.Views;

public partial class WorkOrderDialog : Window
{
    private readonly WorkOrderDialogViewModel _viewModel;
    public WorkOrderDialog(WorkOrderDialogViewModel viewModel) { InitializeComponent(); _viewModel = viewModel; DataContext = viewModel; }
    public WorkOrderDraft? Result { get; private set; }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.TryCreateDraft(out var draft)) return;
        Result = draft; DialogResult = true;
    }
}
