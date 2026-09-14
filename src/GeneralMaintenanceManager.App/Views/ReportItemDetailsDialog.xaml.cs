using System.Windows;
using GeneralMaintenanceManager.App.Services;
using GeneralMaintenanceManager.App.ViewModels;

namespace GeneralMaintenanceManager.App.Views;

public partial class ReportItemDetailsDialog : Window
{
    private readonly GlobalHistoryItemViewModel _item;
    private readonly IPrintService _printService;
    private readonly ILocalizationService _localization;

    public ReportItemDetailsDialog(GlobalHistoryItemViewModel item, IPrintService printService, ILocalizationService localization)
    {
        _item = item ?? throw new ArgumentNullException(nameof(item));
        _printService = printService ?? throw new ArgumentNullException(nameof(printService));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        DataContext = _item;
        InitializeComponent();
        NativeWindowAppearance.Attach(this);
        ResponsiveWindowSizing.Attach(this);
    }

    private void Print_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var scope = string.IsNullOrWhiteSpace(_item.ReferenceNumber) ? _item.DateText : _item.ReferenceNumber;
            _printService.PrintGlobalHistory([_item], scope);
        }
        catch (Exception ex)
        {
            ModernMessageDialog.ShowError(this, _localization.GetString("ErrorPrintFailedTitle"), ex.Message, _localization.CurrentCulture);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
