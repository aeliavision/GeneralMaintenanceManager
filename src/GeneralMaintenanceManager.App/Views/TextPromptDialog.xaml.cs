using System.Windows;
using GeneralMaintenanceManager.App.Services;
using GeneralMaintenanceManager.App.ViewModels;

namespace GeneralMaintenanceManager.App.Views;

public partial class TextPromptDialog : Window
{
    private readonly TextPromptDialogViewModel _viewModel;
    private readonly string _requiredMessage;

    public TextPromptDialog(TextPromptDialogViewModel viewModel, string requiredMessage)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _requiredMessage = requiredMessage ?? string.Empty;
        InitializeComponent();
        DataContext = _viewModel;
        NativeWindowAppearance.Attach(this);
        ResponsiveWindowSizing.Attach(this);
    }

    public string? Result { get; private set; }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.ValidateRequired(_requiredMessage)) return;
        Result = _viewModel.Value.Trim();
        DialogResult = true;
    }
}
