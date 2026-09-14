using System.Windows;
using GeneralMaintenanceManager.App.Services;
using GeneralMaintenanceManager.App.ViewModels;
using GeneralMaintenanceManager.Core.Models;

namespace GeneralMaintenanceManager.App.Views;

public partial class MaintenanceRecordDialog : Window
{
    private readonly MaintenanceRecordDialogViewModel _viewModel;

    public MaintenanceRecordDialog(MaintenanceRecordDialogViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DataContext = _viewModel;
        NativeWindowAppearance.Attach(this);
        ResponsiveWindowSizing.Attach(this);
    }

    public MaintenanceRecordEditDraft? EditResult { get; private set; }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.TryCreateEditDraft(out var editDraft) || editDraft is null) return;
        EditResult = editDraft;
        DialogResult = true;
    }
}
