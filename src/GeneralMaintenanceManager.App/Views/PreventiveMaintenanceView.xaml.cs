using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GeneralMaintenanceManager.App.ViewModels;

namespace GeneralMaintenanceManager.App.Views;

public partial class PreventiveMaintenanceView : UserControl
{
    public PreventiveMaintenanceView() => InitializeComponent();

    private void MaintenancePlansGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (!IsDataGridRow(e.OriginalSource as DependencyObject)) return;
        if (DataContext is MainViewModel viewModel && viewModel.EditMaintenancePlanCommand.CanExecute(null))
            viewModel.EditMaintenancePlanCommand.Execute(null);
    }

    private static bool IsDataGridRow(DependencyObject? source)
    {
        while (source is not null && source is not DataGridRow)
            source = VisualTreeHelper.GetParent(source);
        return source is DataGridRow;
    }
}
