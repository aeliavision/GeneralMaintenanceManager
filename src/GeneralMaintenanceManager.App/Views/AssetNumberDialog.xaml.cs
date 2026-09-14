using System.Windows;
using GeneralMaintenanceManager.App.Services;
using GeneralMaintenanceManager.App.ViewModels;
using GeneralMaintenanceManager.Core.Models;

namespace GeneralMaintenanceManager.App.Views;

public partial class AssetNumberDialog : Window
{
    private readonly AssetNumberDialogViewModel _viewModel;

    public AssetNumberDialog(AssetNumberDialogViewModel viewModel)
    {
        InitializeComponent();
        NativeWindowAppearance.Attach(this);
        ResponsiveWindowSizing.Attach(this);
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    public AssetNumberChangeDraft? Result { get; private set; }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.TryCreateDraft(out var draft))
        {
            return;
        }
        Result = draft;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
