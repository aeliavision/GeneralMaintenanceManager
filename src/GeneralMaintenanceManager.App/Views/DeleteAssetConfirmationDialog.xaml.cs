using System.Windows;
using System.Windows.Markup;
using GeneralMaintenanceManager.App.Services;

namespace GeneralMaintenanceManager.App.Views;

public partial class DeleteAssetConfirmationDialog : Window
{
    private const string ConfirmationKeyword = "DELETE";

    public DeleteAssetConfirmationDialog(
        string assetDisplay,
        string assetName,
        ILocalizationService localization)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetDisplay);
        ArgumentNullException.ThrowIfNull(assetName);
        ArgumentNullException.ThrowIfNull(localization);

        InitializeComponent();
        NativeWindowAppearance.Attach(this);
        ResponsiveWindowSizing.Attach(this);

        FlowDirection = localization.CurrentCulture.TextInfo.IsRightToLeft
            ? FlowDirection.RightToLeft
            : FlowDirection.LeftToRight;
        Language = XmlLanguage.GetLanguage(localization.CurrentCulture.IetfLanguageTag);
        AssetIdentityText.Text = localization.Format(
            "DeleteConfirmationAssetFormat",
            assetDisplay,
            assetName);

        Loaded += (_, _) => ConfirmationTextBox.Focus();
    }

    private void ConfirmationTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        ConfirmDeleteButton.IsEnabled = string.Equals(
            ConfirmationTextBox.Text.Trim(),
            ConfirmationKeyword,
            StringComparison.Ordinal);
    }

    private void ConfirmDelete_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDeleteButton.IsEnabled)
        {
            return;
        }

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
