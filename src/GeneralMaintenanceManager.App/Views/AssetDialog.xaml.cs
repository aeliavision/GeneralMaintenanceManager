using System.Windows;
using GeneralMaintenanceManager.App.Services;
using GeneralMaintenanceManager.App.ViewModels;
using GeneralMaintenanceManager.Core.Entities;
using GeneralMaintenanceManager.Core.Models;

namespace GeneralMaintenanceManager.App.Views;

public partial class AssetDialog : Window
{
    private readonly AssetDialogViewModel _viewModel;
    private readonly ILocalizationService _localization;
    private readonly Asset? _assets;

    public AssetDialog(
        AssetDialogViewModel viewModel,
        ILocalizationService localization,
        Asset? asset)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(localization);

        InitializeComponent();
        NativeWindowAppearance.Attach(this);
        ResponsiveWindowSizing.Attach(this);
        _viewModel = viewModel;
        _localization = localization;
        _assets = asset;
        DataContext = viewModel;
    }

    public AssetDraft? Result { get; private set; }

    public bool DeleteRequested { get; private set; }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.TryCreateDraft(out var draft))
        {
            return;
        }

        Result = draft;
        DialogResult = true;
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_assets is null)
        {
            return;
        }

        var assetDisplay = string.IsNullOrWhiteSpace(_assets.AssetNumber)
            ? _localization.GetString("UnassignedAsset")
            : $"#{_assets.AssetNumber}";

        var confirmationDialog = new DeleteAssetConfirmationDialog(
            assetDisplay,
            _assets.AssetName,
            _localization)
        {
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        if (confirmationDialog.ShowDialog() != true)
        {
            return;
        }

        DeleteRequested = true;
        Result = null;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
