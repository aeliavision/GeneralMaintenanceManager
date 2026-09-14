using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using GeneralMaintenanceManager.App.Services;

namespace GeneralMaintenanceManager.App.Views;

public enum ModernMessageDialogKind
{
    Information,
    Warning,
    Error,
    Confirmation
}

public partial class ModernMessageDialog : Window
{
    private ModernMessageDialog(
        string title,
        string message,
        ModernMessageDialogKind kind,
        CultureInfo? culture)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(message);

        InitializeComponent();
        NativeWindowAppearance.Attach(this);
        ResponsiveWindowSizing.Attach(this);

        Title = title;
        HeadingText.Text = title;
        MessageText.Text = message;

        culture ??= CultureInfo.CurrentUICulture;
        FlowDirection = culture.TextInfo.IsRightToLeft
            ? FlowDirection.RightToLeft
            : FlowDirection.LeftToRight;
        Language = XmlLanguage.GetLanguage(culture.IetfLanguageTag);

        ConfigureKind(kind);
        Loaded += (_, _) => PrimaryButton.Focus();
    }

    public static bool ShowConfirmation(
        Window? owner,
        string title,
        string message,
        CultureInfo? culture = null)
    {
        var dialog = CreateOwned(owner, title, message, ModernMessageDialogKind.Confirmation, culture);
        return dialog.ShowDialog() == true;
    }

    public static void ShowInformation(
        Window? owner,
        string title,
        string message,
        CultureInfo? culture = null)
    {
        var dialog = CreateOwned(owner, title, message, ModernMessageDialogKind.Information, culture);
        dialog.ShowDialog();
    }

    public static void ShowWarning(
        Window? owner,
        string title,
        string message,
        CultureInfo? culture = null)
    {
        var dialog = CreateOwned(owner, title, message, ModernMessageDialogKind.Warning, culture);
        dialog.ShowDialog();
    }

    public static void ShowError(
        Window? owner,
        string title,
        string message,
        CultureInfo? culture = null)
    {
        var dialog = CreateOwned(owner, title, message, ModernMessageDialogKind.Error, culture);
        dialog.ShowDialog();
    }

    private static ModernMessageDialog CreateOwned(
        Window? owner,
        string title,
        string message,
        ModernMessageDialogKind kind,
        CultureInfo? culture)
    {
        var dialog = new ModernMessageDialog(title, message, kind, culture);
        if (owner is { IsLoaded: true, IsVisible: true })
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        return dialog;
    }

    private void ConfigureKind(ModernMessageDialogKind kind)
    {
        var (softBrushKey, borderBrushKey, foregroundBrushKey, eyebrowKey, icon) = kind switch
        {
            ModernMessageDialogKind.Error => (
                "ErrorSoftBrush",
                "ErrorBorderBrush",
                "ErrorDarkBrush",
                "DialogErrorEyebrow",
                "!"),
            ModernMessageDialogKind.Warning => (
                "WarningSoftBrush",
                "WarningBorderBrush",
                "WarningDarkBrush",
                "DialogWarningEyebrow",
                "!"),
            ModernMessageDialogKind.Confirmation => (
                "WarningSoftBrush",
                "WarningBorderBrush",
                "WarningDarkBrush",
                "DialogConfirmationEyebrow",
                "?"),
            _ => (
                "SecondarySoftBrush",
                "SecondaryBorderBrush",
                "SecondaryBrush",
                "DialogInformationEyebrow",
                "i")
        };

        IconBorder.Background = (Brush)FindResource(softBrushKey);
        IconBorder.BorderBrush = (Brush)FindResource(borderBrushKey);
        IconText.Foreground = (Brush)FindResource(foregroundBrushKey);
        IconText.Text = icon;
        EyebrowText.Foreground = (Brush)FindResource(foregroundBrushKey);
        EyebrowText.SetResourceReference(TextBlock.TextProperty, eyebrowKey);

        if (kind == ModernMessageDialogKind.Confirmation)
        {
            CancelButton.Visibility = Visibility.Visible;
            PrimaryButton.SetResourceReference(ContentControl.ContentProperty, "DialogContinue");
            return;
        }

        CancelButton.Visibility = Visibility.Collapsed;
        PrimaryButton.SetResourceReference(ContentControl.ContentProperty, "DialogOk");
    }

    private void Primary_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
