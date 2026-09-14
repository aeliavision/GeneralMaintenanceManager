using System.ComponentModel;
using System.Windows;
using System.Windows.Markup;
using GeneralMaintenanceManager.App.Services;
using GeneralMaintenanceManager.App.ViewModels;
using GeneralMaintenanceManager.App.Views;

namespace GeneralMaintenanceManager.App;

public partial class MainWindow : Window
{
    private readonly ILocalizationService _localization;

    public MainWindow(MainViewModel viewModel, ILocalizationService localization)
    {
        _localization = localization;
        InitializeComponent();
        NativeWindowAppearance.Attach(this);
        ResponsiveWindowSizing.Attach(this);
        Language = XmlLanguage.GetLanguage(localization.CurrentCulture.IetfLanguageTag);
        FlowDirection = localization.CurrentCulture.TextInfo.IsRightToLeft
            ? System.Windows.FlowDirection.RightToLeft
            : System.Windows.FlowDirection.LeftToRight;
        DataContext = viewModel;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (DataContext is MainViewModel { IsBusy: true })
        {
            e.Cancel = true;
            ModernMessageDialog.ShowInformation(
                this,
                _localization.GetString("AppTitle"),
                _localization.GetString("BusyCloseMessage"),
                _localization.CurrentCulture);
        }

        base.OnClosing(e);
    }
}
