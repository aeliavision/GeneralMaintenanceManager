using System.Windows;
using GeneralMaintenanceManager.App.Models;
using GeneralMaintenanceManager.App.Services;
using GeneralMaintenanceManager.App.ViewModels;

namespace GeneralMaintenanceManager.App.Views;

public partial class MaintenanceRecordDetailsDialog : Window
{
    public MaintenanceRecordDetailsDialog(MaintenanceRecordDetailsDialogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        NativeWindowAppearance.Attach(this);
        ResponsiveWindowSizing.Attach(this);
    }

    public MaintenanceRecordDetailsAction Action { get; private set; } = MaintenanceRecordDetailsAction.Close;

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        Action = MaintenanceRecordDetailsAction.Edit;
        DialogResult = true;
    }

    private void Void_Click(object sender, RoutedEventArgs e)
    {
        Action = MaintenanceRecordDetailsAction.MarkInvalid;
        DialogResult = true;
    }

    private void Print_Click(object sender, RoutedEventArgs e)
    {
        Action = MaintenanceRecordDetailsAction.Print;
        DialogResult = true;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
