using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GeneralMaintenanceManager.App.ViewModels;
using GeneralMaintenanceManager.Core.Entities;

namespace GeneralMaintenanceManager.App.Views;

public partial class AssetsView : UserControl
{
    public AssetsView() => InitializeComponent();

    private void AssetGrid_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid grid || e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        if (ItemsControl.ContainerFromElement(grid, source) is not DataGridRow row
            || row.Item is not Asset asset
            || DataContext is not MainViewModel viewModel)
        {
            return;
        }

        viewModel.SelectedAsset = asset;
        if (!viewModel.AddMaintenanceCommand.CanExecute(null))
        {
            return;
        }

        viewModel.AddMaintenanceCommand.Execute(null);
        e.Handled = true;
    }
}
