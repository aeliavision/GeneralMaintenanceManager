using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GeneralMaintenanceManager.App.ViewModels;

namespace GeneralMaintenanceManager.App.Views;

public partial class MaintenanceView : UserControl
{
    public MaintenanceView() => InitializeComponent();

    private void MaintenanceGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (!IsDataGridRow(e.OriginalSource as DependencyObject)) return;
        if (DataContext is MainViewModel viewModel && viewModel.OpenMaintenanceLogDetailsCommand.CanExecute(null))
            viewModel.OpenMaintenanceLogDetailsCommand.Execute(null);
    }

    private static bool IsDataGridRow(DependencyObject? source)
    {
        while (source is not null && source is not DataGridRow)
            source = VisualTreeHelper.GetParent(source);
        return source is DataGridRow;
    }
}
