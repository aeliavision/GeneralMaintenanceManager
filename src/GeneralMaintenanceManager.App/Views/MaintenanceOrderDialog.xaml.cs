using System.Windows;
using GeneralMaintenanceManager.App.ViewModels;
using GeneralMaintenanceManager.App.Services;
using GeneralMaintenanceManager.Core.Models;

namespace GeneralMaintenanceManager.App.Views;

public partial class MaintenanceOrderDialog : Window
{
    private readonly MaintenanceOrderDialogViewModel _viewModel;

    public MaintenanceOrderDialog(MaintenanceOrderDialogViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DataContext = _viewModel;
        NativeWindowAppearance.Attach(this);
        ResponsiveWindowSizing.Attach(this);
    }

    public WorkOrderDraft? Result { get; private set; }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.TryCreateDraft(out var draft) || draft is null) return;
        Result = draft;
        DialogResult = true;
    }
}
