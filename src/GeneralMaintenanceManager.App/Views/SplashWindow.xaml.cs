using System.Globalization;
using System.Windows;
using GeneralMaintenanceManager.App.Services;

namespace GeneralMaintenanceManager.App.Views;

public partial class SplashWindow : Window
{
    public SplashWindow()
    {
        InitializeComponent();
        ResponsiveWindowSizing.Attach(this);
    }

    public void SetProgress(int percentage, string status)
    {
        var clamped = Math.Clamp(percentage, 0, 100);
        StartupProgress.Value = clamped;
        ProgressText.Text = string.Create(CultureInfo.CurrentCulture, $"{clamped}%");
        StatusText.Text = status ?? string.Empty;
    }
}
