using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GeneralMaintenanceManager.App.ViewModels;

namespace GeneralMaintenanceManager.App.Views;

public partial class ReportsView : UserControl
{
    public ReportsView() => InitializeComponent();

    private void ReportGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (!IsDataGridRow(e.OriginalSource as DependencyObject)) return;
        if (DataContext is MainViewModel viewModel && viewModel.OpenGlobalHistoryDetailsCommand.CanExecute(null))
            viewModel.OpenGlobalHistoryDetailsCommand.Execute(null);
    }

    private static bool IsDataGridRow(DependencyObject? source)
    {
        while (source is not null && source is not DataGridRow)
            source = VisualTreeHelper.GetParent(source);
        return source is DataGridRow;
    }
}
