using System.Windows;
using GeneralMaintenanceManager.App.Services;
using GeneralMaintenanceManager.App.ViewModels;

namespace GeneralMaintenanceManager.App.Views;

public partial class AuditDialog : Window
{
    public AuditDialog(AuditDialogViewModel viewModel)
    {
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        NativeWindowAppearance.Attach(this);
        ResponsiveWindowSizing.Attach(this);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
