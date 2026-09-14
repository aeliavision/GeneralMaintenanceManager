using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GeneralMaintenanceManager.App.Collections;
using GeneralMaintenanceManager.App.Models;
using GeneralMaintenanceManager.App.Services;
using GeneralMaintenanceManager.Core.Abstractions;
using GeneralMaintenanceManager.Core.Entities;
using GeneralMaintenanceManager.Core.Enums;
using GeneralMaintenanceManager.Core.Models;
using GeneralMaintenanceManager.Infrastructure.Data;
using GeneralMaintenanceManager.Infrastructure.Services;

namespace GeneralMaintenanceManager.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly IAssetService _assetService;
    private readonly IMaintenanceHistoryService _historyService;
    private readonly IExcelImportService _excelImportService;
    private readonly IAssetExportService _assetExportService;
    private readonly IDataLocationService _dataLocationService;
    private readonly IDialogService _dialogService;
    private readonly IPrintService _printService;
    private readonly IBackupService _backupService;
    private readonly IReviewService _reviewService;
    private readonly ILocalizationService _localization;
    private readonly IUserSettingsService _userSettingsService;
    private readonly IWorkOrderService _workOrderService;
    private readonly IMaintenancePlanService _maintenancePlanService;
    private readonly IMaintenanceRecordService _maintenanceRecordService;
    private readonly IActivityHistoryService _activityHistoryService;
    private readonly IGlobalHistoryQueryService _globalHistoryQueryService;
    private readonly IDiagnosticLogger _diagnosticLogger;
    private readonly IDatabaseMaintenanceService _databaseMaintenanceService;
    private readonly DatabaseActivityGate _databaseActivityGate;
    private readonly BatchObservableCollection<Asset> _assets = [];
    private readonly BatchObservableCollection<HistoryItemViewModel> _history = [];
    private readonly BatchObservableCollection<GlobalHistoryItemViewModel> _globalHistory = [];
    private readonly BatchObservableCollection<ReviewCaseViewModel> _reviewItems = [];
    private readonly BatchObservableCollection<WorkOrder> _workOrders = [];
    private readonly BatchObservableCollection<MaintenancePlan> _maintenancePlans = [];
    private MaintenanceDashboardSummary _dashboardSummary = new(DateTime.Today.Year, 0, 0m, [], [], []);
    private readonly BatchObservableCollection<GlobalHistoryItemViewModel> _maintenanceLogItems = [];
    private readonly BatchObservableCollection<GlobalHistoryItemViewModel> _operationalReportItems = [];
    private readonly HashSet<Guid> _reviewDeferredForVisit = [];
    private readonly List<MaintenanceRecord> _currentHistoryRecords = [];
    private MaintenanceEntrySuggestionCatalog _maintenanceEntrySuggestionCatalog = MaintenanceEntrySuggestionCatalog.Empty;
    private MaintenanceEntrySuggestionCatalog _workOrderEntrySuggestionCatalog = MaintenanceEntrySuggestionCatalog.Empty;
    private CancellationTokenSource? _historyCancellation;
    private CancellationTokenSource? _globalHistoryCancellation;
    private GlobalHistoryPageCursor? _globalHistoryCursor;
    private bool _hasMoreGlobalHistory;
    private bool _maintenanceSuggestionsLoaded;
    private bool _workOrderSuggestionsLoaded;
    private readonly SemaphoreSlim _historyReadGate = new(1, 1);
    private ICollectionView? _assetsView;
    private ICollectionView? _globalHistoryView;
    private Asset? _selectedAsset;
    private YearOption _selectedYear;
    private string _searchText = string.Empty;
    private string _historySearchText = string.Empty;
    private DateTime? _historyFromDate;
    private DateTime? _historyToDate;
    private string _historySiteFilter = string.Empty;
    private string _historyLocationFilter = string.Empty;
    private string _historyMaintenanceTypeFilter = string.Empty;
    private string _historyActivityTypeFilter = string.Empty;
    private string _historyPerformedByFilter = string.Empty;
    private string _maintenanceSearchText = string.Empty;
    private DateTime? _maintenanceFromDate = DateTime.Today;
    private DateTime? _maintenanceToDate = DateTime.Today;
    private string _maintenanceLocationFilter = string.Empty;
    private string _maintenanceTypeFilter = string.Empty;
    private string _maintenancePerformedByFilter = string.Empty;
    private string _selectedHistoryMode = "Maintenance";
    private string _statusText;
    private string _selectedLanguageCode;
    private IReadOnlyList<LanguageOption> _languages;
    private bool _isBusy;
    private bool _isImporting;
    private int _importProgressPercent;
    private string _importProgressText = string.Empty;
    private bool _isBackupOperationRunning;
    private int _backupProgressPercent;
    private string _backupProgressText = string.Empty;
    private int _repairCount;
    private decimal _repairCost;
    private bool _suppressHistoryReload;
    private bool _isDashboardView = true;
    private bool _isMaintenanceView;
    private bool _isWorkOrdersView;
    private bool _isPreventiveView;
    private bool _isSettingsView;
    private bool _isReviewView;
    private bool _isReportsView;
    private bool _isWorkOrdersEnabled;
    private bool _isPreventiveMaintenanceEnabled;
    private bool _isAssetTrackingEnabled;
    private string _newCustomMaintenanceType = string.Empty;
    private string? _selectedCustomMaintenanceType;
    private string _maintenanceTypeSettingsMessage = string.Empty;
    private ReviewCaseViewModel? _selectedReviewItem;
    private WorkOrder? _selectedWorkOrder;
    private WorkOrderDashboardMetrics _workOrderDashboardMetrics = new(0, 0, 0);
    private WorkOrderPageCursor? _workOrderPageCursor;
    private WorkOrderPageCursor? _workOrderNextCursor;
    private readonly Stack<WorkOrderPageCursor?> _workOrderPreviousCursors = new();
    private string _workOrderSearchText = string.Empty;
    private string _workOrderListMode = "Open";
    private MaintenancePlan? _selectedMaintenancePlan;
    private GlobalHistoryItemViewModel? _selectedGlobalHistoryItem;
    private GlobalHistoryItemViewModel? _selectedMaintenanceLogItem;
    private string _maintenanceReprintNumber = string.Empty;
    private string _operationalReportSearchText = string.Empty;
    private DateTime? _operationalReportFromDate;
    private DateTime? _operationalReportToDate;
    private bool _isSidebarExpanded = true;
    private int _dueMaintenancePlanCount;
    private readonly string _applicationVersion = GetApplicationVersion();

    public MainViewModel(
        IAssetService assetService,
        IMaintenanceHistoryService historyService,
        IExcelImportService excelImportService,
        IAssetExportService assetExportService,
        IDataLocationService dataLocationService,
        IDialogService dialogService,
        IPrintService printService,
        IBackupService backupService,
        IReviewService reviewService,
        ILocalizationService localization,
        IUserSettingsService userSettingsService,
        IWorkOrderService workOrderService,
        IMaintenancePlanService maintenancePlanService,
        IMaintenanceRecordService maintenanceRecordService,
        IActivityHistoryService activityHistoryService,
        IGlobalHistoryQueryService globalHistoryQueryService,
        IDiagnosticLogger diagnosticLogger,
        IDatabaseMaintenanceService databaseMaintenanceService,
        DatabaseActivityGate databaseActivityGate)
    {
        _assetService = assetService;
        _historyService = historyService;
        _excelImportService = excelImportService;
        _assetExportService = assetExportService;
        _dataLocationService = dataLocationService;
        _dialogService = dialogService;
        _printService = printService;
        _backupService = backupService;
        _reviewService = reviewService;
        _localization = localization;
        _userSettingsService = userSettingsService;
        _workOrderService = workOrderService;
        _maintenancePlanService = maintenancePlanService;
        _maintenanceRecordService = maintenanceRecordService;
        _activityHistoryService = activityHistoryService;
        _globalHistoryQueryService = globalHistoryQueryService;
        _diagnosticLogger = diagnosticLogger;
        _databaseMaintenanceService = databaseMaintenanceService;
        _databaseActivityGate = databaseActivityGate;
        _isWorkOrdersEnabled = _userSettingsService.LoadWorkOrdersEnabled();
        _isPreventiveMaintenanceEnabled = _userSettingsService.LoadPreventiveMaintenanceEnabled();
        _isAssetTrackingEnabled = _userSettingsService.LoadAssetTrackingEnabled();
        foreach (var maintenanceType in _userSettingsService.LoadCustomMaintenanceTypes())
        {
            CustomMaintenanceTypes.Add(maintenanceType);
        }

        _selectedYear = CreateAllYearsOption();
        Years.Add(_selectedYear);
        _statusText = _localization.GetString("StatusReady");
        _selectedLanguageCode = _localization.CurrentLanguageCode;
        _languages = CreateLanguageOptions();

        RefreshCommand = new AsyncRelayCommand(LoadAsync);
        AddMaintenanceCommand = new AsyncRelayCommand(AddMaintenanceRecordAsync, () => !IsBusy);
        AddDashboardMaintenanceCommand = new AsyncRelayCommand(AddMaintenanceRecordAsync, () => !IsBusy);
        AddAssetCommand = new AsyncRelayCommand(AddAssetAsync, () => !IsBusy);
        EditAssetCommand = new AsyncRelayCommand(EditAssetAsync, () => SelectedAsset is not null && !IsBusy);
        ImportExcelCommand = new AsyncRelayCommand(ImportExcelAsync, () => !IsBusy);
        ExportAssetExcelCommand = new AsyncRelayCommand(ExportAssetExcelAsync, () => !IsBusy);
        ExportAssetCsvCommand = new AsyncRelayCommand(ExportAssetCsvAsync, () => !IsBusy);
        OpenDataFolderCommand = new RelayCommand(OpenDataFolder);
        ShowDashboardCommand = new RelayCommand(() => SetActiveView("Dashboard"));
        ShowAssetCommand = new AsyncRelayCommand(ShowAssetAsync, () => IsAssetTrackingEnabled && !IsBusy);
        ShowMaintenanceCommand = new RelayCommand(() =>
        {
            SetActiveView("Maintenance");
            _ = LoadGlobalHistorySafelyAsync();
        });
        ShowMaintenanceReportCommand = new RelayCommand(() =>
        {
            SetHistoryMode("Maintenance");
            SetActiveView("Reports");
            _ = LoadGlobalHistorySafelyAsync();
        });
        SetHistoryModeCommand = new RelayCommand<string>(SetHistoryMode);
        ShowWorkOrdersCommand = new AsyncRelayCommand(ShowWorkOrdersAsync, () => !IsBusy);
        SetWorkOrderListModeCommand = new AsyncRelayCommand<string>(SetWorkOrderListModeAsync, mode => !IsBusy && IsKnownWorkOrderListMode(mode));
        ShowPreventiveCommand = new AsyncRelayCommand(ShowPreventiveAsync, () => IsPreventiveMaintenanceEnabled && !IsBusy);
        ShowReviewCommand = new AsyncRelayCommand(ShowReviewAsync, () => !IsBusy);
        ShowReportsCommand = new RelayCommand(() =>
        {
            SetHistoryMode("Maintenance");
            SetActiveView("Reports");
            _ = LoadGlobalHistorySafelyAsync();
        });
        ShowSettingsCommand = new RelayCommand(() =>
        {
            SetActiveView("Settings");
            _ = RefreshDatabaseStatusAsync();
        });
        AddCustomMaintenanceTypeCommand = new RelayCommand(AddCustomMaintenanceType, () => !string.IsNullOrWhiteSpace(NewCustomMaintenanceType));
        RemoveCustomMaintenanceTypeCommand = new RelayCommand(RemoveSelectedCustomMaintenanceType, () => SelectedCustomMaintenanceType is not null);
        ToggleSidebarCommand = new RelayCommand(() => IsSidebarExpanded = !IsSidebarExpanded);
        PrintFullHistoryCommand = new AsyncRelayCommand(PrintFullHistoryAsync, () => SelectedAsset is not null && !IsBusy);
        PrintHistoryItemCommand = new AsyncRelayCommand<HistoryItemViewModel>(PrintHistoryItemAsync, item => item is not null && SelectedAsset is not null && !IsBusy);
        EditMaintenanceCommand = new AsyncRelayCommand<HistoryItemViewModel>(EditMaintenanceAsync, item => item is { IsMaintenance: true } && SelectedAsset is not null && !IsBusy);
        ViewAuditCommand = new AsyncRelayCommand<HistoryItemViewModel>(ViewAuditAsync, item => item is { HasAudit: true } && SelectedAsset is not null && !IsBusy);
        CreateBackupCommand = new AsyncRelayCommand(CreateBackupAsync, () => !IsBusy);
        RestoreBackupCommand = new AsyncRelayCommand(RestoreBackupAsync, () => !IsBusy);
        OpenBackupFolderCommand = new RelayCommand(OpenBackupFolder);
        EditReviewACommand = new AsyncRelayCommand<ReviewCaseViewModel>(EditReviewAAsync, item => item is not null && !IsBusy);
        EditReviewBCommand = new AsyncRelayCommand<ReviewCaseViewModel>(EditReviewBAsync, item => item?.AssetB is not null && !IsBusy);
        AssignAssetACommand = new AsyncRelayCommand<ReviewCaseViewModel>(AssignAssetAAsync, item => item is not null && !IsBusy);
        AssignAssetBCommand = new AsyncRelayCommand<ReviewCaseViewModel>(AssignAssetBAsync, item => item?.AssetB is not null && !IsBusy);
        KeepBothCommand = new AsyncRelayCommand<ReviewCaseViewModel>(KeepBothAsync, item => item?.AssetB is not null && !IsBusy);
        MergeIntoACommand = new AsyncRelayCommand<ReviewCaseViewModel>(MergeIntoAAsync, item => item?.AssetB is not null && !IsBusy);
        MergeIntoBCommand = new AsyncRelayCommand<ReviewCaseViewModel>(MergeIntoBAsync, item => item?.AssetB is not null && !IsBusy);
        ReviewLaterCommand = new AsyncRelayCommand<ReviewCaseViewModel>(ReviewLaterAsync, item => item is not null && !IsBusy);
        NewWorkOrderCommand = new AsyncRelayCommand(NewWorkOrderAsync, () => IsWorkOrdersEnabled && !IsBusy);
        RefreshWorkOrdersCommand = new AsyncRelayCommand(RefreshWorkOrdersAsync, () => !IsBusy);
        PreviousWorkOrderPageCommand = new AsyncRelayCommand(PreviousWorkOrderPageAsync, () => CanGoToPreviousWorkOrderPage && !IsBusy);
        NextWorkOrderPageCommand = new AsyncRelayCommand(NextWorkOrderPageAsync, () => CanGoToNextWorkOrderPage && !IsBusy);
        EditOrViewWorkOrderCommand = new AsyncRelayCommand(EditOrViewSelectedWorkOrderAsync, () => SelectedWorkOrder is not null && !IsBusy);
        PrintWorkOrderCommand = new RelayCommand(PrintSelectedWorkOrder, () => SelectedWorkOrder is not null && !IsBusy);
        AssignWorkOrderCommand = new AsyncRelayCommand(AssignSelectedWorkOrderAsync, () => IsWorkOrdersEnabled && SelectedWorkOrder is not null && !IsBusy && SelectedWorkOrder.Status is WorkOrderStatus.New or WorkOrderStatus.Assigned or WorkOrderStatus.OnHold);
        StartWorkOrderCommand = new AsyncRelayCommand(StartSelectedWorkOrderAsync, () => IsWorkOrdersEnabled && SelectedWorkOrder is not null && !IsBusy && SelectedWorkOrder.Status == WorkOrderStatus.Assigned);
        ResumeWorkOrderCommand = new AsyncRelayCommand(ResumeSelectedWorkOrderAsync, () => IsWorkOrdersEnabled && SelectedWorkOrder is not null && !IsBusy && SelectedWorkOrder.Status == WorkOrderStatus.OnHold);
        HoldWorkOrderCommand = new AsyncRelayCommand(HoldSelectedWorkOrderAsync, () => IsWorkOrdersEnabled && SelectedWorkOrder is not null && !IsBusy && SelectedWorkOrder.Status == WorkOrderStatus.InProgress);
        CompleteWorkOrderCommand = new AsyncRelayCommand(CompleteSelectedWorkOrderAsync, () => SelectedWorkOrder is not null && !IsBusy && !SelectedWorkOrder.IsClosed);
        CancelWorkOrderCommand = new AsyncRelayCommand(CancelSelectedWorkOrderAsync, () => SelectedWorkOrder is not null && !IsBusy && SelectedWorkOrder.Status is not WorkOrderStatus.Completed and not WorkOrderStatus.Cancelled);
        NewMaintenancePlanCommand = new AsyncRelayCommand(NewMaintenancePlanAsync, () => !IsBusy);
        EditMaintenancePlanCommand = new AsyncRelayCommand(EditSelectedMaintenancePlanAsync, () => SelectedMaintenancePlan is not null && !IsBusy);
        GeneratePlanWorkOrderCommand = new AsyncRelayCommand(GenerateSelectedPlanWorkOrderAsync, () => SelectedMaintenancePlan is { IsActive: true } && !IsBusy);
        TogglePlanActiveCommand = new AsyncRelayCommand(ToggleSelectedPlanActiveAsync, () => SelectedMaintenancePlan is not null && !IsBusy);
        PrintMaintenancePlanCommand = new AsyncRelayCommand(PrintSelectedMaintenancePlanAsync, () => SelectedMaintenancePlan is not null && !IsBusy);
        OpenGlobalHistoryAssetCommand = new AsyncRelayCommand(OpenGlobalHistoryAssetAsync, () => SelectedGlobalHistoryItem?.HasAsset == true && !IsBusy);
        OpenGlobalHistoryDetailsCommand = new AsyncRelayCommand(OpenSelectedGlobalHistoryDetailsAsync, () => SelectedGlobalHistoryItem is not null && !IsBusy);
        OpenMaintenanceLogDetailsCommand = new AsyncRelayCommand(OpenSelectedMaintenanceLogDetailsAsync, () => SelectedMaintenanceLogItem is not null && !IsBusy);
        PrintGlobalHistoryCommand = new RelayCommand(PrintGlobalHistory, () => GlobalHistoryCount > 0 && !IsBusy);
        LoadMoreGlobalHistoryCommand = new AsyncRelayCommand(LoadMoreGlobalHistoryAsync, () => HasMoreGlobalHistory && !IsBusy);
        ReprintMaintenanceByNumberCommand = new AsyncRelayCommand(ReprintMaintenanceByNumberAsync, () => !IsBusy && !string.IsNullOrWhiteSpace(MaintenanceReprintNumber));
        PrintOperationalReportCommand = new RelayCommand(PrintOperationalReport, () => OperationalReportCount > 0 && !IsBusy);

        GlobalHistoryView = CollectionViewSource.GetDefaultView(_globalHistory);
        InitializeProductionSettings();

        _localization.LanguageChanged += OnLanguageChanged;
    }

    public ObservableCollection<HistoryItemViewModel> History => _history;

    public ObservableCollection<GlobalHistoryItemViewModel> GlobalHistory => _globalHistory;

    public ObservableCollection<ReviewCaseViewModel> ReviewItems => _reviewItems;


    public ObservableCollection<WorkOrder> WorkOrders => _workOrders;

    public ObservableCollection<MaintenancePlan> MaintenancePlans => _maintenancePlans;

    public ObservableCollection<GlobalHistoryItemViewModel> MaintenanceLogItems => _maintenanceLogItems;

    public ObservableCollection<GlobalHistoryItemViewModel> OperationalReportItems => _operationalReportItems;

    public ObservableCollection<string> CustomMaintenanceTypes { get; } = [];

    public ObservableCollection<YearOption> Years { get; } = [];

    public IReadOnlyList<LanguageOption> Languages
    {
        get => _languages;
        private set => SetProperty(ref _languages, value);
    }

    public IAsyncRelayCommand RefreshCommand { get; }

    public IAsyncRelayCommand AddMaintenanceCommand { get; }

    public IAsyncRelayCommand AddDashboardMaintenanceCommand { get; }

    public IAsyncRelayCommand AddAssetCommand { get; }

    public IAsyncRelayCommand EditAssetCommand { get; }

    public IAsyncRelayCommand ImportExcelCommand { get; }

    public IAsyncRelayCommand ExportAssetExcelCommand { get; }

    public IAsyncRelayCommand ExportAssetCsvCommand { get; }

    public IRelayCommand OpenDataFolderCommand { get; }

    public IRelayCommand ShowDashboardCommand { get; }

    public IAsyncRelayCommand ShowAssetCommand { get; }

    public IRelayCommand ShowMaintenanceCommand { get; }

    public IRelayCommand ShowMaintenanceReportCommand { get; }

    public IRelayCommand<string> SetHistoryModeCommand { get; }

    public IAsyncRelayCommand ShowWorkOrdersCommand { get; }

    public IAsyncRelayCommand<string> SetWorkOrderListModeCommand { get; }

    public IAsyncRelayCommand ShowPreventiveCommand { get; }

    public IAsyncRelayCommand ShowReviewCommand { get; }

    public IRelayCommand ShowReportsCommand { get; }

    public IRelayCommand ShowSettingsCommand { get; }

    public IRelayCommand AddCustomMaintenanceTypeCommand { get; }

    public IRelayCommand RemoveCustomMaintenanceTypeCommand { get; }

    public IRelayCommand ToggleSidebarCommand { get; }

    public IAsyncRelayCommand PrintFullHistoryCommand { get; }

    public IAsyncRelayCommand<HistoryItemViewModel> PrintHistoryItemCommand { get; }

    public IAsyncRelayCommand<HistoryItemViewModel> EditMaintenanceCommand { get; }

    public IAsyncRelayCommand<HistoryItemViewModel> ViewAuditCommand { get; }

    public IAsyncRelayCommand CreateBackupCommand { get; }

    public IAsyncRelayCommand RestoreBackupCommand { get; }

    public IRelayCommand OpenBackupFolderCommand { get; }

    public IAsyncRelayCommand<ReviewCaseViewModel> EditReviewACommand { get; }
    public IAsyncRelayCommand<ReviewCaseViewModel> EditReviewBCommand { get; }
    public IAsyncRelayCommand<ReviewCaseViewModel> AssignAssetACommand { get; }
    public IAsyncRelayCommand<ReviewCaseViewModel> AssignAssetBCommand { get; }
    public IAsyncRelayCommand<ReviewCaseViewModel> KeepBothCommand { get; }
    public IAsyncRelayCommand<ReviewCaseViewModel> MergeIntoACommand { get; }
    public IAsyncRelayCommand<ReviewCaseViewModel> MergeIntoBCommand { get; }
    public IAsyncRelayCommand<ReviewCaseViewModel> ReviewLaterCommand { get; }
    public IAsyncRelayCommand NewWorkOrderCommand { get; }
    public IAsyncRelayCommand RefreshWorkOrdersCommand { get; }
    public IAsyncRelayCommand PreviousWorkOrderPageCommand { get; }
    public IAsyncRelayCommand NextWorkOrderPageCommand { get; }
    public IAsyncRelayCommand EditOrViewWorkOrderCommand { get; }
    public IRelayCommand PrintWorkOrderCommand { get; }
    public IAsyncRelayCommand AssignWorkOrderCommand { get; }

    public IAsyncRelayCommand StartWorkOrderCommand { get; }
    public IAsyncRelayCommand ResumeWorkOrderCommand { get; }

    public IAsyncRelayCommand HoldWorkOrderCommand { get; }
    public IAsyncRelayCommand CompleteWorkOrderCommand { get; }
    public IAsyncRelayCommand CancelWorkOrderCommand { get; }
    public IAsyncRelayCommand NewMaintenancePlanCommand { get; }
    public IAsyncRelayCommand EditMaintenancePlanCommand { get; }
    public IAsyncRelayCommand GeneratePlanWorkOrderCommand { get; }
    public IAsyncRelayCommand TogglePlanActiveCommand { get; }
    public IAsyncRelayCommand PrintMaintenancePlanCommand { get; }
    public IAsyncRelayCommand OpenGlobalHistoryAssetCommand { get; }
    public IAsyncRelayCommand OpenGlobalHistoryDetailsCommand { get; }
    public IAsyncRelayCommand OpenMaintenanceLogDetailsCommand { get; }
    public IRelayCommand PrintGlobalHistoryCommand { get; }
    public IAsyncRelayCommand LoadMoreGlobalHistoryCommand { get; }

    public IAsyncRelayCommand ReprintMaintenanceByNumberCommand { get; }
    public IRelayCommand PrintOperationalReportCommand { get; }

    public bool IsSidebarExpanded
    {
        get => _isSidebarExpanded;
        private set => SetProperty(ref _isSidebarExpanded, value);
    }

    public bool IsDashboardView => _isDashboardView;

    public bool IsMaintenanceView => _isMaintenanceView;

    public bool IsWorkOrdersView => _isWorkOrdersView;

    public string WorkOrderListMode => _workOrderListMode;

    public bool IsWorkOrderListOpen => string.Equals(_workOrderListMode, "Open", StringComparison.Ordinal);

    public bool IsWorkOrderListCompleted => string.Equals(_workOrderListMode, "Completed", StringComparison.Ordinal);

    public bool IsWorkOrderListCancelled => string.Equals(_workOrderListMode, "Cancelled", StringComparison.Ordinal);

    public bool IsWorkOrderListAll => string.Equals(_workOrderListMode, "All", StringComparison.Ordinal);

    public bool IsPreventiveView => _isPreventiveView;

    public bool IsSettingsView => _isSettingsView;

    public bool IsReviewView => _isReviewView;

    public bool IsReportsView => _isReportsView;

    public bool IsAssetView => !_isDashboardView && !_isMaintenanceView && !_isWorkOrdersView && !_isPreventiveView && !_isSettingsView && !_isReviewView && !_isReportsView;

    public bool IsAssetWorkspaceView => IsAssetView;

    public string AssetWorkspaceTitle => _localization.GetString("AssetRegistry");

    public string AssetWorkspaceSubtitle => _localization.GetString("AssetRegistrySubtitle");

    public string AssetWorkspaceEyebrow => _localization.GetString("AssetHistoryEyebrow");

    public string NewCustomMaintenanceType
    {
        get => _newCustomMaintenanceType;
        set
        {
            if (!SetProperty(ref _newCustomMaintenanceType, value ?? string.Empty)) return;
            AddCustomMaintenanceTypeCommand.NotifyCanExecuteChanged();
        }
    }

    public string? SelectedCustomMaintenanceType
    {
        get => _selectedCustomMaintenanceType;
        set
        {
            if (!SetProperty(ref _selectedCustomMaintenanceType, value)) return;
            RemoveCustomMaintenanceTypeCommand.NotifyCanExecuteChanged();
        }
    }

    public string MaintenanceTypeSettingsMessage
    {
        get => _maintenanceTypeSettingsMessage;
        private set => SetProperty(ref _maintenanceTypeSettingsMessage, value ?? string.Empty);
    }

    private static IReadOnlyList<string> GetBuiltInMaintenanceTypeNames() => MaintenanceTypeCatalog.BuiltIn;

    public bool IsWorkOrdersEnabled
    {
        get => _isWorkOrdersEnabled;
        set
        {
            if (_isWorkOrdersEnabled == value) return;
            try
            {
                _userSettingsService.SaveWorkOrdersEnabled(value);
                SetProperty(ref _isWorkOrdersEnabled, value);
                OnPropertyChanged(nameof(HasOptionalFeaturesEnabled));
                ShowWorkOrdersCommand.NotifyCanExecuteChanged();
                NewWorkOrderCommand.NotifyCanExecuteChanged();
                AssignWorkOrderCommand.NotifyCanExecuteChanged();
                StartWorkOrderCommand.NotifyCanExecuteChanged();
                ResumeWorkOrderCommand.NotifyCanExecuteChanged();
                HoldWorkOrderCommand.NotifyCanExecuteChanged();
                _ = ReloadOperationsAfterFeatureChangeAsync();
            }
            catch (Exception ex)
            {
                OnPropertyChanged(nameof(IsWorkOrdersEnabled));
                _dialogService.ShowError(_localization.GetString("SettingsSaveFailedTitle"), GetExceptionMessage(ex));
            }
        }
    }

    public bool IsPreventiveMaintenanceEnabled
    {
        get => _isPreventiveMaintenanceEnabled;
        set
        {
            if (_isPreventiveMaintenanceEnabled == value) return;
            try
            {
                _userSettingsService.SavePreventiveMaintenanceEnabled(value);
                SetProperty(ref _isPreventiveMaintenanceEnabled, value);
                OnPropertyChanged(nameof(HasOptionalFeaturesEnabled));
                if (!value && IsPreventiveView) SetActiveView("Dashboard");
                _ = ReloadOperationsAfterFeatureChangeAsync();
            }
            catch (Exception ex)
            {
                OnPropertyChanged(nameof(IsPreventiveMaintenanceEnabled));
                _dialogService.ShowError(_localization.GetString("SettingsSaveFailedTitle"), GetExceptionMessage(ex));
            }
        }
    }

    public bool IsAssetTrackingEnabled
    {
        get => _isAssetTrackingEnabled;
        set
        {
            if (_isAssetTrackingEnabled == value) return;
            try
            {
                _userSettingsService.SaveAssetTrackingEnabled(value);
                SetProperty(ref _isAssetTrackingEnabled, value);
                OnPropertyChanged(nameof(HasOptionalFeaturesEnabled));
                ShowAssetCommand.NotifyCanExecuteChanged();
                if (!value && IsAssetView) SetActiveView("Dashboard");
                if (value) _ = LoadAssetWorkspaceAfterFeatureEnableAsync();
            }
            catch (Exception ex)
            {
                OnPropertyChanged(nameof(IsAssetTrackingEnabled));
                _dialogService.ShowError(_localization.GetString("SettingsSaveFailedTitle"), GetExceptionMessage(ex));
            }
        }
    }

    public bool HasOptionalFeaturesEnabled => IsWorkOrdersEnabled || IsPreventiveMaintenanceEnabled || IsAssetTrackingEnabled;

    public int InServiceAssetCount => _assets.Count(item => string.Equals(item.OperationalStatus, "In Service", StringComparison.OrdinalIgnoreCase));

    public int UnderMaintenanceAssetCount => _assets.Count(item =>
        string.Equals(item.OperationalStatus, "Under Maintenance", StringComparison.OrdinalIgnoreCase)
        || string.Equals(item.OperationalStatus, "Under Repair", StringComparison.OrdinalIgnoreCase));

    public int OutOfServiceAssetCount => _assets.Count(item =>
        string.Equals(item.OperationalStatus, "Out of Service", StringComparison.OrdinalIgnoreCase)
        || string.Equals(item.OperationalStatus, "Retired", StringComparison.OrdinalIgnoreCase)
        || string.Equals(item.OperationalStatus, "Disposed", StringComparison.OrdinalIgnoreCase));

    public long MaintenanceThisMonthCount => _dashboardSummary.MonthCount;
    public decimal MaintenanceCostThisMonth => _dashboardSummary.MonthCost;
    public IReadOnlyList<MaintenanceRecord> RecentMaintenanceRecords => _dashboardSummary.RecentRecords;

    public IReadOnlyList<GlobalHistoryItemViewModel> RecentMaintenanceSummaryItems => _dashboardSummary.RecentRecords
        .Take(5)
        .Select(item => new GlobalHistoryItemViewModel(item, _localization))
        .ToArray();

    public IReadOnlyList<SummaryMetricItem> DashboardTopLocations => _dashboardSummary.TopLocations
        .Select(item => new SummaryMetricItem(
            item.Label,
            item.Count.ToString("N0", _localization.CurrentCulture),
            item.TotalCost.ToString("N2", _localization.CurrentCulture)))
        .ToArray();

    public IReadOnlyList<SummaryMetricItem> DashboardTopMaintenanceTypes => _dashboardSummary.TopMaintenanceTypes
        .Select(item => new SummaryMetricItem(
            MaintenanceTextPresentation.LocalizeMaintenanceType(item.Label, _localization),
            item.Count.ToString("N0", _localization.CurrentCulture),
            item.TotalCost.ToString("N2", _localization.CurrentCulture)))
        .ToArray();

    public long OpenWorkOrderCount => _workOrderDashboardMetrics.OpenCount;
    public long UrgentWorkOrderCount => _workOrderDashboardMetrics.UrgentOpenCount;
    public long OverdueWorkOrderCount => _workOrderDashboardMetrics.OverdueOpenCount;

    public string WorkOrderSearchText
    {
        get => _workOrderSearchText;
        set => SetProperty(ref _workOrderSearchText, value ?? string.Empty);
    }

    public bool CanGoToPreviousWorkOrderPage => _workOrderPreviousCursors.Count > 0;
    public bool CanGoToNextWorkOrderPage => _workOrderNextCursor is not null;
    public int DueMaintenancePlanCount => _dueMaintenancePlanCount;

    public int ReviewCount => ReviewItems.Count;

    public WorkOrder? SelectedWorkOrder
    {
        get => _selectedWorkOrder;
        set
        {
            if (!SetProperty(ref _selectedWorkOrder, value)) return;
            EditOrViewWorkOrderCommand.NotifyCanExecuteChanged();
            PrintWorkOrderCommand.NotifyCanExecuteChanged();
            AssignWorkOrderCommand.NotifyCanExecuteChanged();
            StartWorkOrderCommand.NotifyCanExecuteChanged();
            ResumeWorkOrderCommand.NotifyCanExecuteChanged();
            HoldWorkOrderCommand.NotifyCanExecuteChanged();
            CompleteWorkOrderCommand.NotifyCanExecuteChanged();
            CancelWorkOrderCommand.NotifyCanExecuteChanged();
        }
    }

    public MaintenancePlan? SelectedMaintenancePlan
    {
        get => _selectedMaintenancePlan;
        set
        {
            if (!SetProperty(ref _selectedMaintenancePlan, value)) return;
            EditMaintenancePlanCommand.NotifyCanExecuteChanged();
            GeneratePlanWorkOrderCommand.NotifyCanExecuteChanged();
            TogglePlanActiveCommand.NotifyCanExecuteChanged();
            PrintMaintenancePlanCommand.NotifyCanExecuteChanged();
        }
    }

    public ReviewCaseViewModel? SelectedReviewItem
    {
        get => _selectedReviewItem;
        set
        {
            if (!SetProperty(ref _selectedReviewItem, value))
            {
                return;
            }
            EditReviewACommand.NotifyCanExecuteChanged();
            EditReviewBCommand.NotifyCanExecuteChanged();
            AssignAssetACommand.NotifyCanExecuteChanged();
            AssignAssetBCommand.NotifyCanExecuteChanged();
            KeepBothCommand.NotifyCanExecuteChanged();
            MergeIntoACommand.NotifyCanExecuteChanged();
            MergeIntoBCommand.NotifyCanExecuteChanged();
            ReviewLaterCommand.NotifyCanExecuteChanged();
        }
    }

    public int MaintenanceLogCount => _maintenanceLogItems.Count;

    public string OperationalReportSearchText
    {
        get => _operationalReportSearchText;
        set
        {
            if (!SetProperty(ref _operationalReportSearchText, value ?? string.Empty)) return;
            RebuildOperationalReport();
        }
    }

    public DateTime? OperationalReportFromDate
    {
        get => _operationalReportFromDate;
        set
        {
            if (!SetProperty(ref _operationalReportFromDate, value)) return;
            RebuildOperationalReport();
        }
    }

    public DateTime? OperationalReportToDate
    {
        get => _operationalReportToDate;
        set
        {
            if (!SetProperty(ref _operationalReportToDate, value)) return;
            RebuildOperationalReport();
        }
    }

    public int OperationalReportCount => _operationalReportItems.Count;
    public decimal OperationalReportCost => _operationalReportItems.Sum(item => item.CostValue ?? 0m);
    public int OperationalReportLocationCount => _operationalReportItems
        .Select(item => item.LocationDisplay)
        .Where(item => !string.IsNullOrWhiteSpace(item))
        .Distinct(StringComparer.CurrentCultureIgnoreCase)
        .Count();

    public decimal OperationalReportAverageCost => OperationalReportCount == 0
        ? 0m
        : OperationalReportCost / OperationalReportCount;

    public IReadOnlyList<SummaryMetricItem> OperationalReportTopLocations => _operationalReportItems
        .Where(item => !string.IsNullOrWhiteSpace(item.LocationDisplay))
        .GroupBy(item => item.LocationDisplay.Trim(), StringComparer.CurrentCultureIgnoreCase)
        .OrderByDescending(group => group.Count())
        .ThenBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase)
        .Take(5)
        .Select(group => new SummaryMetricItem(
            group.Key,
            group.Count().ToString("N0", _localization.CurrentCulture),
            (group.Sum(item => item.CostValue ?? 0m)).ToString("N2", _localization.CurrentCulture)))
        .ToArray();

    public IReadOnlyList<SummaryMetricItem> OperationalReportTopMaintenanceTypes => _operationalReportItems
        .Where(item => !string.IsNullOrWhiteSpace(item.TypeText))
        .GroupBy(item => item.TypeText.Trim(), StringComparer.CurrentCultureIgnoreCase)
        .OrderByDescending(group => group.Count())
        .ThenBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase)
        .Take(6)
        .Select(group => new SummaryMetricItem(
            group.Key,
            group.Count().ToString("N0", _localization.CurrentCulture),
            (group.Sum(item => item.CostValue ?? 0m)).ToString("N2", _localization.CurrentCulture)))
        .ToArray();

    public string MaintenanceReprintNumber
    {
        get => _maintenanceReprintNumber;
        set
        {
            if (!SetProperty(ref _maintenanceReprintNumber, value ?? string.Empty)) return;
            ReprintMaintenanceByNumberCommand.NotifyCanExecuteChanged();
        }
    }

    public string DataDirectory => _dataLocationService.DataDirectory;

    public string BackupsDirectory => _backupService.BackupsDirectory;

    public string ApplicationVersion => _applicationVersion;

    public string SelectedLanguageCode
    {
        get => _selectedLanguageCode;
        set
        {
            // WPF ComboBox can transiently push null while the localized ItemsSource is replaced.
            // Keep the active language in that moment rather than accidentally switching to English.
            var normalized = string.IsNullOrWhiteSpace(value)
                ? _localization.CurrentLanguageCode
                : string.Equals(value, "ar", StringComparison.OrdinalIgnoreCase) ? "ar" : "en";
            if (string.Equals(_selectedLanguageCode, normalized, StringComparison.OrdinalIgnoreCase)) return;
            try
            {
                _userSettingsService.SaveLanguageCode(normalized);
                SetProperty(ref _selectedLanguageCode, normalized);
                _localization.SetLanguage(normalized);
            }
            catch (Exception ex)
            {
                OnPropertyChanged(nameof(SelectedLanguageCode));
                _dialogService.ShowError(_localization.GetString("SettingsSaveFailedTitle"), GetExceptionMessage(ex));
            }
        }
    }

    public ICollectionView? AssetView
    {
        get => _assetsView;
        private set => SetProperty(ref _assetsView, value);
    }

    public ICollectionView? GlobalHistoryView
    {
        get => _globalHistoryView;
        private set => SetProperty(ref _globalHistoryView, value);
    }

    public GlobalHistoryItemViewModel? SelectedGlobalHistoryItem
    {
        get => _selectedGlobalHistoryItem;
        set
        {
            if (!SetProperty(ref _selectedGlobalHistoryItem, value))
            {
                return;
            }

            OpenGlobalHistoryAssetCommand.NotifyCanExecuteChanged();
            OpenGlobalHistoryDetailsCommand.NotifyCanExecuteChanged();
            OpenMaintenanceLogDetailsCommand.NotifyCanExecuteChanged();
        }
    }

    public GlobalHistoryItemViewModel? SelectedMaintenanceLogItem
    {
        get => _selectedMaintenanceLogItem;
        set
        {
            if (!SetProperty(ref _selectedMaintenanceLogItem, value)) return;
            OpenMaintenanceLogDetailsCommand.NotifyCanExecuteChanged();
        }
    }

    public Asset? SelectedAsset
    {
        get => _selectedAsset;
        set
        {
            if (!SetProperty(ref _selectedAsset, value))
            {
                return;
            }

            AddMaintenanceCommand.NotifyCanExecuteChanged();
            AddDashboardMaintenanceCommand.NotifyCanExecuteChanged();
            EditAssetCommand.NotifyCanExecuteChanged();
            PrintFullHistoryCommand.NotifyCanExecuteChanged();
            PrintHistoryItemCommand.NotifyCanExecuteChanged();
            EditMaintenanceCommand.NotifyCanExecuteChanged();
            ViewAuditCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(HasSelectedAsset));
            OnPropertyChanged(nameof(SelectedAssetHasNotes));
            OnPropertyChanged(nameof(SelectedAssetDisplayNotes));
            OnPropertyChanged(nameof(SelectedAssetLabel));
            if (!_suppressHistoryReload)
            {
                if (IsReportsView)
                {
                    _ = LoadGlobalHistorySafelyAsync();
                }
                else
                {
                    _ = LoadHistorySafelyAsync();
                }
            }
        }
    }

    public bool HasSelectedAsset => SelectedAsset is not null;

    public string SelectedAssetDisplayNotes => CleanAssetNotesForDisplay(SelectedAsset?.Notes);

    public bool SelectedAssetHasNotes => SelectedAssetDisplayNotes.Length > 0;

    private static string CleanAssetNotesForDisplay(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes))
        {
            return string.Empty;
        }

        var parts = notes
            .Split(" | ", StringSplitOptions.None)
            .Select(part => part.Trim())
            .Where(part => part.Length > 0
                && !string.Equals(part, "ü", StringComparison.OrdinalIgnoreCase));

        return string.Join(" | ", parts);
    }

    [AllowNull]
    public YearOption SelectedYear
    {
        get => _selectedYear;
        set
        {
            // WPF ComboBox can transiently push null into SelectedItem while Years is rebuilt.
            var normalizedValue = value
                ?? Years.FirstOrDefault(item => item.Year is null)
                ?? CreateAllYearsOption();

            if (!SetProperty(ref _selectedYear, normalizedValue))
            {
                return;
            }

            if (!_suppressHistoryReload)
            {
                // Maintenance and Reports are global/canonical Maintenance views.
                // A year change must re-query the selected annual partition (or the
                // bounded cross-year merge for All years), not only Asset history.
                if (IsMaintenanceView || IsReportsView)
                {
                    _ = LoadGlobalHistorySafelyAsync();
                }
                else
                {
                    _ = LoadHistorySafelyAsync();
                }
            }
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value ?? string.Empty))
            {
                AssetView?.Refresh();
            }
        }
    }

    public string SelectedHistoryMode
    {
        get => _selectedHistoryMode;
        private set
        {
            if (!SetProperty(ref _selectedHistoryMode, value)) return;
            GlobalHistoryView?.Refresh();
            OnPropertyChanged(nameof(IsHistoryMaintenanceMode));
            OnPropertyChanged(nameof(IsHistoryWorkOrdersMode));
            OnPropertyChanged(nameof(IsHistoryPreventiveMode));
            OnPropertyChanged(nameof(IsHistoryAllActivityMode));
            NotifyGlobalHistoryPresentationChanged();
        }
    }

    public bool IsHistoryMaintenanceMode => string.Equals(SelectedHistoryMode, "Maintenance", StringComparison.Ordinal);
    public bool IsHistoryWorkOrdersMode => string.Equals(SelectedHistoryMode, "WorkOrders", StringComparison.Ordinal);
    public bool IsHistoryPreventiveMode => string.Equals(SelectedHistoryMode, "PreventiveMaintenance", StringComparison.Ordinal);
    public bool IsHistoryAllActivityMode => string.Equals(SelectedHistoryMode, "AllActivity", StringComparison.Ordinal);

    public string HistorySearchText
    {
        get => _historySearchText;
        set
        {
            if (!SetProperty(ref _historySearchText, value ?? string.Empty)) return;
            _ = LoadGlobalHistorySafelyAsync();
        }
    }

    public DateTime? HistoryFromDate
    {
        get => _historyFromDate;
        set { if (SetProperty(ref _historyFromDate, value)) _ = LoadGlobalHistorySafelyAsync(); }
    }

    public DateTime? HistoryToDate
    {
        get => _historyToDate;
        set { if (SetProperty(ref _historyToDate, value)) _ = LoadGlobalHistorySafelyAsync(); }
    }

    public string HistorySiteFilter
    {
        get => _historySiteFilter;
        set { if (SetProperty(ref _historySiteFilter, value ?? string.Empty)) _ = LoadGlobalHistorySafelyAsync(); }
    }

    public string HistoryLocationFilter
    {
        get => _historyLocationFilter;
        set { if (SetProperty(ref _historyLocationFilter, value ?? string.Empty)) _ = LoadGlobalHistorySafelyAsync(); }
    }

    public string HistoryMaintenanceTypeFilter
    {
        get => _historyMaintenanceTypeFilter;
        set { if (SetProperty(ref _historyMaintenanceTypeFilter, value ?? string.Empty)) _ = LoadGlobalHistorySafelyAsync(); }
    }

    public string HistoryActivityTypeFilter
    {
        get => _historyActivityTypeFilter;
        set { if (SetProperty(ref _historyActivityTypeFilter, value ?? string.Empty)) _ = LoadGlobalHistorySafelyAsync(); }
    }

    public string HistoryPerformedByFilter
    {
        get => _historyPerformedByFilter;
        set { if (SetProperty(ref _historyPerformedByFilter, value ?? string.Empty)) _ = LoadGlobalHistorySafelyAsync(); }
    }

    public IReadOnlyList<string> HistorySiteSuggestions => BuildHistorySuggestions(item => item.Site);
    public IReadOnlyList<string> HistoryLocationSuggestions => BuildHistorySuggestions(item => item.Location);
    public IReadOnlyList<string> HistoryMaintenanceTypeSuggestions => BuildHistorySuggestions(
        item => item.TypeText,
        item => item.SourceMaintenanceRecord is not null);
    public IReadOnlyList<string> HistoryActivityTypeSuggestions => BuildHistorySuggestions(
        item => item.TypeText,
        item => item.SourceActivity is not null);
    public IReadOnlyList<string> HistoryPerformedBySuggestions => BuildHistorySuggestions(item => item.PerformedBy);

    public string MaintenanceSearchText
    {
        get => _maintenanceSearchText;
        set { if (SetProperty(ref _maintenanceSearchText, value ?? string.Empty)) _ = LoadGlobalHistorySafelyAsync(); }
    }

    public DateTime? MaintenanceFromDate
    {
        get => _maintenanceFromDate;
        set { if (SetProperty(ref _maintenanceFromDate, value)) _ = LoadGlobalHistorySafelyAsync(); }
    }

    public DateTime? MaintenanceToDate
    {
        get => _maintenanceToDate;
        set { if (SetProperty(ref _maintenanceToDate, value)) _ = LoadGlobalHistorySafelyAsync(); }
    }

    public string MaintenanceLocationFilter
    {
        get => _maintenanceLocationFilter;
        set { if (SetProperty(ref _maintenanceLocationFilter, value ?? string.Empty)) _ = LoadGlobalHistorySafelyAsync(); }
    }

    public string MaintenanceTypeFilter
    {
        get => _maintenanceTypeFilter;
        set { if (SetProperty(ref _maintenanceTypeFilter, value ?? string.Empty)) _ = LoadGlobalHistorySafelyAsync(); }
    }

    public string MaintenancePerformedByFilter
    {
        get => _maintenancePerformedByFilter;
        set { if (SetProperty(ref _maintenancePerformedByFilter, value ?? string.Empty)) _ = LoadGlobalHistorySafelyAsync(); }
    }

    public IReadOnlyList<string> MaintenanceLocationSuggestions => BuildMaintenanceSuggestions(item => item.Location);
    public IReadOnlyList<string> MaintenanceTypeSuggestions => BuildMaintenanceSuggestions(item => item.TypeText);
    public IReadOnlyList<string> MaintenancePerformedBySuggestions => BuildMaintenanceSuggestions(item => item.PerformedBy);

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value))
            {
                return;
            }

            AddMaintenanceCommand.NotifyCanExecuteChanged();
            AddDashboardMaintenanceCommand.NotifyCanExecuteChanged();
            ShowWorkOrdersCommand.NotifyCanExecuteChanged();
            SetWorkOrderListModeCommand.NotifyCanExecuteChanged();
            AddAssetCommand.NotifyCanExecuteChanged();
            EditAssetCommand.NotifyCanExecuteChanged();
            ImportExcelCommand.NotifyCanExecuteChanged();
            ExportAssetExcelCommand.NotifyCanExecuteChanged();
            ExportAssetCsvCommand.NotifyCanExecuteChanged();
            PrintFullHistoryCommand.NotifyCanExecuteChanged();
            PrintHistoryItemCommand.NotifyCanExecuteChanged();
            EditMaintenanceCommand.NotifyCanExecuteChanged();
            ViewAuditCommand.NotifyCanExecuteChanged();
            CreateBackupCommand.NotifyCanExecuteChanged();
            RestoreBackupCommand.NotifyCanExecuteChanged();
            ShowReviewCommand.NotifyCanExecuteChanged();
            ShowAssetCommand.NotifyCanExecuteChanged();
            EditReviewACommand.NotifyCanExecuteChanged();
            EditReviewBCommand.NotifyCanExecuteChanged();
            AssignAssetACommand.NotifyCanExecuteChanged();
            AssignAssetBCommand.NotifyCanExecuteChanged();
            KeepBothCommand.NotifyCanExecuteChanged();
            MergeIntoACommand.NotifyCanExecuteChanged();
            MergeIntoBCommand.NotifyCanExecuteChanged();
            ReviewLaterCommand.NotifyCanExecuteChanged();
            NewWorkOrderCommand.NotifyCanExecuteChanged();
            RefreshWorkOrdersCommand.NotifyCanExecuteChanged();
            PreviousWorkOrderPageCommand.NotifyCanExecuteChanged();
            NextWorkOrderPageCommand.NotifyCanExecuteChanged();
            EditOrViewWorkOrderCommand.NotifyCanExecuteChanged();
            PrintWorkOrderCommand.NotifyCanExecuteChanged();
            AssignWorkOrderCommand.NotifyCanExecuteChanged();
            StartWorkOrderCommand.NotifyCanExecuteChanged();
            ResumeWorkOrderCommand.NotifyCanExecuteChanged();
            HoldWorkOrderCommand.NotifyCanExecuteChanged();
            CompleteWorkOrderCommand.NotifyCanExecuteChanged();
            CancelWorkOrderCommand.NotifyCanExecuteChanged();
            NewMaintenancePlanCommand.NotifyCanExecuteChanged();
            EditMaintenancePlanCommand.NotifyCanExecuteChanged();
            GeneratePlanWorkOrderCommand.NotifyCanExecuteChanged();
            TogglePlanActiveCommand.NotifyCanExecuteChanged();
            PrintMaintenancePlanCommand.NotifyCanExecuteChanged();
            OpenGlobalHistoryAssetCommand.NotifyCanExecuteChanged();
            OpenGlobalHistoryDetailsCommand.NotifyCanExecuteChanged();
            PrintGlobalHistoryCommand.NotifyCanExecuteChanged();
            LoadMoreGlobalHistoryCommand.NotifyCanExecuteChanged();
            ReprintMaintenanceByNumberCommand.NotifyCanExecuteChanged();
            PrintOperationalReportCommand.NotifyCanExecuteChanged();
        }
    }

    public bool IsImporting
    {
        get => _isImporting;
        private set => SetProperty(ref _isImporting, value);
    }

    public int ImportProgressPercent
    {
        get => _importProgressPercent;
        private set => SetProperty(ref _importProgressPercent, Math.Clamp(value, 0, 100));
    }

    public string ImportProgressText
    {
        get => _importProgressText;
        private set => SetProperty(ref _importProgressText, value ?? string.Empty);
    }

    public bool IsBackupOperationRunning
    {
        get => _isBackupOperationRunning;
        private set => SetProperty(ref _isBackupOperationRunning, value);
    }

    public int BackupProgressPercent
    {
        get => _backupProgressPercent;
        private set => SetProperty(ref _backupProgressPercent, Math.Clamp(value, 0, 100));
    }

    public string BackupProgressText
    {
        get => _backupProgressText;
        private set => SetProperty(ref _backupProgressText, value ?? string.Empty);
    }

    public int AssetCount => _assets.Count;

    public string AssetCountLabel => _localization.Format("CountAssetsFormat", AssetCount);

    public int HistoryCount => History.Count;

    public string HistoryCountLabel => _localization.Format("CountHistoryItemsFormat", HistoryCount);

    public int GlobalHistoryCount => GlobalHistoryView?.Cast<object>().Count() ?? GlobalHistory.Count;
    public bool HasMoreGlobalHistory
    {
        get => _hasMoreGlobalHistory;
        private set
        {
            if (!SetProperty(ref _hasMoreGlobalHistory, value)) return;
            LoadMoreGlobalHistoryCommand.NotifyCanExecuteChanged();
        }
    }
    private IReadOnlyList<GlobalHistoryItemViewModel> VisibleReportItems =>
        GlobalHistoryView?.Cast<object>().OfType<GlobalHistoryItemViewModel>().ToArray()
        ?? GlobalHistory.ToArray();

    public int UnifiedReportLocationCount => VisibleReportItems
        .Select(item => item.LocationDisplay)
        .Where(item => !string.IsNullOrWhiteSpace(item))
        .Distinct(StringComparer.CurrentCultureIgnoreCase)
        .Count();

    public int UnifiedReportPeopleCount => VisibleReportItems
        .Select(item => item.PerformedBy)
        .Where(item => !string.IsNullOrWhiteSpace(item))
        .Distinct(StringComparer.CurrentCultureIgnoreCase)
        .Count();

    public decimal UnifiedReportCost => VisibleReportItems.Sum(item => item.CostValue ?? 0m);

    public IReadOnlyList<SummaryMetricItem> UnifiedReportTopLocations => VisibleReportItems
        .Where(item => !string.IsNullOrWhiteSpace(item.LocationDisplay))
        .GroupBy(item => item.LocationDisplay.Trim(), StringComparer.CurrentCultureIgnoreCase)
        .OrderByDescending(group => group.Count())
        .ThenBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase)
        .Take(3)
        .Select(group => new SummaryMetricItem(
            group.Key,
            group.Count().ToString("N0", _localization.CurrentCulture),
            group.Sum(item => item.CostValue ?? 0m).ToString("N2", _localization.CurrentCulture)))
        .ToArray();

    public IReadOnlyList<SummaryMetricItem> UnifiedReportTopTypes => VisibleReportItems
        .Where(item => !string.IsNullOrWhiteSpace(item.TypeText))
        .GroupBy(item => item.TypeText.Trim(), StringComparer.CurrentCultureIgnoreCase)
        .OrderByDescending(group => group.Count())
        .ThenBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase)
        .Take(3)
        .Select(group => new SummaryMetricItem(
            group.Key,
            group.Count().ToString("N0", _localization.CurrentCulture),
            group.Sum(item => item.CostValue ?? 0m).ToString("N2", _localization.CurrentCulture)))
        .ToArray();


    public int GlobalHistoryAssetCount => GlobalHistory.Where(item => item.AssetId.HasValue).Select(item => item.AssetId!.Value).Distinct().Count();

    public decimal GlobalHistoryCost => GlobalHistory.Sum(item => item.CostValue ?? 0m);

    public int MaintenanceCount
    {
        get => _repairCount;
        private set => SetProperty(ref _repairCount, value);
    }

    public decimal MaintenanceCost
    {
        get => _repairCost;
        private set => SetProperty(ref _repairCost, value);
    }

    public string SelectedAssetLabel => SelectedAsset is null
        ? _localization.GetString("NoMachineSelected")
        : string.IsNullOrWhiteSpace(SelectedAsset.AssetNumber)
            ? _localization.GetString("UnassignedAsset")
            : $"#{SelectedAsset.AssetNumber}";

    public async Task InitializeAsync()
    {
        if (IsBusy) return;
        try
        {
            IsBusy = true;
            StatusText = _localization.GetString("SplashLoadingWorkspace");
            await ReloadYearsAsync().ConfigureAwait(true);
            await ReloadOperationsAsync().ConfigureAwait(true);
            StatusText = _localization.GetString("StatusReady");
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task LoadInitialHistoryAsync()
    {
        if (IsAssetTrackingEnabled)
        {
            await ReloadAssetAsync(SelectedAsset?.AssetId).ConfigureAwait(true);
            await ReloadReviewAsync(refreshDetection: false).ConfigureAwait(true);
            await LoadHistorySafelyAsync().ConfigureAwait(true);
        }
    }

    public void Dispose()
    {
        _localization.LanguageChanged -= OnLanguageChanged;

        var cancellation = _historyCancellation;
        _historyCancellation = null;
        cancellation?.Cancel();
        cancellation?.Dispose();

        var globalCancellation = _globalHistoryCancellation;
        _globalHistoryCancellation = null;
        globalCancellation?.Cancel();
        globalCancellation?.Dispose();
        GC.SuppressFinalize(this);
    }

    private void SetActiveView(string view)
    {
        if (string.Equals(view, "Preventive", StringComparison.Ordinal) && !IsPreventiveMaintenanceEnabled) view = "Dashboard";

        _isDashboardView = string.Equals(view, "Dashboard", StringComparison.Ordinal);
        _isMaintenanceView = string.Equals(view, "Maintenance", StringComparison.Ordinal);
        _isWorkOrdersView = string.Equals(view, "WorkOrders", StringComparison.Ordinal);
        _isPreventiveView = string.Equals(view, "Preventive", StringComparison.Ordinal);
        _isSettingsView = string.Equals(view, "Settings", StringComparison.Ordinal);
        _isReviewView = string.Equals(view, "Review", StringComparison.Ordinal);
        _isReportsView = string.Equals(view, "Reports", StringComparison.Ordinal);
        OnPropertyChanged(nameof(IsDashboardView));
        OnPropertyChanged(nameof(IsMaintenanceView));
        OnPropertyChanged(nameof(IsWorkOrdersView));
        OnPropertyChanged(nameof(IsPreventiveView));
        OnPropertyChanged(nameof(IsSettingsView));
        OnPropertyChanged(nameof(IsReviewView));
        OnPropertyChanged(nameof(IsReportsView));
        OnPropertyChanged(nameof(IsAssetView));
        OnPropertyChanged(nameof(IsAssetWorkspaceView));
        OnPropertyChanged(nameof(AssetWorkspaceTitle));
        OnPropertyChanged(nameof(AssetWorkspaceSubtitle));
        OnPropertyChanged(nameof(AssetWorkspaceEyebrow));
    }

    private async Task ShowAssetAsync()
    {
        if (!IsAssetTrackingEnabled) return;
        if (_assets.Count == 0)
        {
            await ReloadAssetAsync(SelectedAsset?.AssetId).ConfigureAwait(true);
            await ReloadReviewAsync(refreshDetection: false).ConfigureAwait(true);
        }
        SetActiveView("Asset");
    }

    private async Task ShowReviewAsync()
    {
        // Review Later hides a case only for the current visit to the Review workspace.
        // When the user leaves and comes back, those still-pending cases should return.
        var isEnteringReview = !IsReviewView;
        if (IsAssetTrackingEnabled && _assets.Count == 0)
        {
            await ReloadAssetAsync(SelectedAsset?.AssetId).ConfigureAwait(true);
            await ReloadReviewAsync(refreshDetection: false).ConfigureAwait(true);
        }
        SetActiveView("Review");

        if (isEnteringReview && _reviewDeferredForVisit.Count > 0)
        {
            _reviewDeferredForVisit.Clear();
            await ReloadReviewAsync(refreshDetection: false).ConfigureAwait(true);
        }
    }

    private Task LoadAsync() => LoadAsync(refreshReviewDetection: true, loadInitialHistory: true);

    private async Task LoadAsync(bool refreshReviewDetection, bool loadInitialHistory)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        StatusText = _localization.GetString("StatusLoadingAsset");

        try
        {
            var selectedAssetId = SelectedAsset?.AssetId;
            if (IsAssetTrackingEnabled)
            {
                await ReloadAssetAsync(selectedAssetId).ConfigureAwait(true);
                await ReloadReviewAsync(refreshReviewDetection).ConfigureAwait(true);
            }
            else
            {
                _assets.ReplaceAll([]);
                _reviewItems.ReplaceAll([]);
                SelectedAsset = null;
                SelectedReviewItem = null;
                OnPropertyChanged(nameof(AssetCount));
                OnPropertyChanged(nameof(ReviewCount));
            }
            await ReloadYearsAsync().ConfigureAwait(true);
            await ReloadOperationsAsync().ConfigureAwait(true);

            if (loadInitialHistory)
            {
                if (IsReportsView)
                {
                    await LoadGlobalHistorySafelyAsync().ConfigureAwait(true);
                }
                else
                {
                    await LoadHistorySafelyAsync().ConfigureAwait(true);
                }
            }

            StatusText = _localization.Format("StatusAssetsLoadedFormat", AssetCount);
        }
        catch (Exception ex)
        {
            _diagnosticLogger.LogError("WorkspaceLoad", ex, "Workspace data load failed.");
            StatusText = _localization.GetString("StatusCouldNotLoadData");
            _dialogService.ShowError(_localization.GetString("ErrorLoadFailedTitle"), ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadAssetWorkspaceAfterFeatureEnableAsync()
    {
        try
        {
            await ReloadAssetAsync(SelectedAsset?.AssetId).ConfigureAwait(true);
            await ReloadReviewAsync(refreshDetection: false).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _diagnosticLogger.LogError("AssetTrackingEnable", ex, "Asset Tracking data could not be loaded after enabling the feature.");
        }
    }

    private async Task RunOperationAsync(Func<Task> operation)
    {
        if (IsBusy) return;
        IsBusy = true;
        try { await operation().ConfigureAwait(true); }
        catch (Exception ex)
        {
            _diagnosticLogger.LogError("Operation", ex, "An interactive operation failed.");
            StatusText = ex.Message;
            _dialogService.ShowError(_localization.GetString("OperationFailed"), ex.Message);
        }
        finally { IsBusy = false; }
    }

    private async Task ReloadYearsAsync()
    {
        var selectedYearValue = _selectedYear.Year;
        var availableYears = await _historyService.GetAvailableYearsAsync().ConfigureAwait(true);

        var previousSuppression = _suppressHistoryReload;
        _suppressHistoryReload = true;
        try
        {
            Years.Clear();
            Years.Add(CreateAllYearsOption());

            foreach (var year in availableYears)
            {
                Years.Add(CreateYearOption(year));
            }

            SelectedYear = selectedYearValue is null
                ? Years[0]
                : Years.FirstOrDefault(item => item.Year == selectedYearValue) ?? Years[0];
        }
        finally
        {
            _suppressHistoryReload = previousSuppression;
        }
    }

    private bool MatchesSearch(object item)
    {
        if (item is not Asset asset)
        {
            return false;
        }

        var search = SearchText.Trim();
        if (search.Length == 0)
        {
            return true;
        }

        return Contains(asset.AssetNumber, search)
            || (string.IsNullOrWhiteSpace(asset.AssetNumber) && Contains(_localization.GetString("UnassignedAsset"), search))
            || Contains(asset.AssetName, search)
            || Contains(asset.Manufacturer, search)
            || Contains(asset.Model, search)
            || Contains(asset.SerialNumber, search)
            || Contains(asset.Site, search)
            || Contains(asset.Location, search);
    }

    private bool MatchesGlobalHistorySearch(object item)
    {
        if (item is not GlobalHistoryItemViewModel historyItem) return false;

        var modeMatches = SelectedHistoryMode switch
        {
            "Maintenance" => historyItem.SourceMaintenanceRecord is not null,
            "WorkOrders" => historyItem.SourceWorkOrder is not null,
            "PreventiveMaintenance" => historyItem.SourceMaintenancePlan is not null,
            "AllActivity" => true,
            _ => true
        };
        if (!modeMatches) return false;

        var from = HistoryFromDate.HasValue ? DateOnly.FromDateTime(HistoryFromDate.Value) : (DateOnly?)null;
        var to = HistoryToDate.HasValue ? DateOnly.FromDateTime(HistoryToDate.Value) : (DateOnly?)null;
        if (from.HasValue && historyItem.RecordDate < from.Value) return false;
        if (to.HasValue && historyItem.RecordDate > to.Value) return false;
        if (!MatchesOptionalFilter(historyItem.Site, HistorySiteFilter)) return false;
        if (!MatchesOptionalFilter(historyItem.Location, HistoryLocationFilter)) return false;
        if (!MatchesOptionalFilter(historyItem.PerformedBy, HistoryPerformedByFilter)) return false;

        if (!string.IsNullOrWhiteSpace(HistoryMaintenanceTypeFilter))
        {
            if (historyItem.SourceMaintenanceRecord is null) return false;
            if (!MatchesOptionalFilter(historyItem.TypeText, HistoryMaintenanceTypeFilter)) return false;
        }

        if (!string.IsNullOrWhiteSpace(HistoryActivityTypeFilter))
        {
            if (historyItem.SourceActivity is null) return false;
            if (!MatchesOptionalFilter(historyItem.TypeText, HistoryActivityTypeFilter)) return false;
        }

        var search = HistorySearchText.Trim();
        if (search.Length == 0) return true;

        return Contains(historyItem.Site, search)
            || Contains(historyItem.Location, search)
            || Contains(historyItem.ReferenceNumber, search)
            || Contains(historyItem.TypeText, search)
            || Contains(historyItem.Title, search)
            || Contains(historyItem.Description, search)
            || Contains(historyItem.PerformedBy, search)
            || Contains(historyItem.Notes, search)
            || Contains(historyItem.AssetName, search)
            || Contains(historyItem.AssetNumber, search);
    }

    private static bool MatchesOptionalFilter(string? value, string filter)
    {
        var normalized = filter.Trim();
        return normalized.Length == 0 || Contains(value, normalized);
    }

    private string[] BuildHistorySuggestions(
        Func<GlobalHistoryItemViewModel, string> selector,
        Func<GlobalHistoryItemViewModel, bool>? predicate = null) =>
        _globalHistory
            .Where(item => predicate?.Invoke(item) ?? true)
            .Select(selector)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

    private static bool Contains(string? value, string search) =>
        value?.Contains(search, StringComparison.OrdinalIgnoreCase) == true;

    private string GetAssetDisplay(Asset asset) =>
        string.IsNullOrWhiteSpace(asset.AssetNumber)
            ? _localization.GetString("UnassignedAsset")
            : asset.AssetNumber;

    private string GetExceptionMessage(Exception exception)
    {
        var messages = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (!string.IsNullOrWhiteSpace(current.Message)
                && !messages.Contains(current.Message, StringComparer.Ordinal))
            {
                messages.Add(current.Message);
            }
        }

        return messages.Count == 0
            ? _localization.GetString("ErrorUnexpected")
            : string.Join(Environment.NewLine, messages);
    }

    private async Task LoadHistorySafelyAsync()
    {
        var currentCancellation = new CancellationTokenSource();
        var previousCancellation = _historyCancellation;
        _historyCancellation = currentCancellation;
        previousCancellation?.Cancel();

        var cancellationToken = currentCancellation.Token;
        var gateAcquired = false;

        try
        {
            _currentHistoryRecords.Clear();
            _history.ReplaceAll([]);
            MaintenanceCount = 0;
            MaintenanceCost = 0m;
            NotifyHistoryPresentationChanged();

            var asset = SelectedAsset;
            if (asset is null)
            {
                return;
            }

            // Snapshot the selection before awaiting so a ComboBox refresh cannot change
            // the requested year while the query is in flight.
            var selectedYear = _selectedYear.Year;

            await _historyReadGate.WaitAsync(cancellationToken).ConfigureAwait(true);
            gateAcquired = true;
            using var databaseLease = await _databaseActivityGate.EnterReadAsync(cancellationToken).ConfigureAwait(true);
            var records = await _historyService.GetHistoryAsync(asset.AssetId, selectedYear, cancellationToken: cancellationToken).ConfigureAwait(true);

            cancellationToken.ThrowIfCancellationRequested();

            _currentHistoryRecords.AddRange(records);
            RebuildHistoryPresentation();

            MaintenanceCount = records.Count(record => !record.IsInvalid);
            MaintenanceCost = records.Where(record => !record.IsInvalid).Sum(record => record.Cost ?? 0m);
        }
        catch (OperationCanceledException)
        {
            // Selection changed before the previous history query completed.
        }
        catch (Exception ex)
        {
            StatusText = _localization.GetString("StatusCouldNotLoadHistory");
            _dialogService.ShowError(_localization.GetString("ErrorHistoryFailedTitle"), ex.Message);
        }
        finally
        {
            if (gateAcquired) _historyReadGate.Release();
            if (ReferenceEquals(_historyCancellation, currentCancellation))
            {
                _historyCancellation = null;
            }

            currentCancellation.Dispose();
        }
    }

    private async Task PrintFullHistoryAsync()
    {
        const int maximumPrintableRecords = 2_000;
        var asset = SelectedAsset;
        if (asset is null)
        {
            return;
        }

        try
        {
            IsBusy = true;
            StatusText = _localization.Format("StatusPreparingFullHistoryPrintFormat", GetAssetDisplay(asset));

            var selection = await Task.Run(() =>
                    _historyService.GetHistoryForPrintAsync(asset.AssetId, maximumPrintableRecords))
                .ConfigureAwait(true);

            if (selection.IsTruncated)
            {
                StatusText = _localization.GetString("StatusPrintCancelled");
                _dialogService.ShowInformation(
                    _localization.GetString("HistoryPrintLimitTitle"),
                    _localization.Format("HistoryPrintLimitMessageFormat", maximumPrintableRecords));
                return;
            }

            var printed = _printService.PrintAssetHistory(asset, selection.Items);
            StatusText = printed
                ? _localization.Format("StatusHistoryPrintSentFormat", GetAssetDisplay(asset))
                : _localization.GetString("StatusPrintCancelled");
        }
        catch (Exception ex)
        {
            StatusText = _localization.GetString("StatusPrintFailed");
            _dialogService.ShowError(
                _localization.GetString("ErrorPrintFailedTitle"),
                GetExceptionMessage(ex));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task PrintHistoryItemAsync(HistoryItemViewModel? historyItem)
    {
        var asset = SelectedAsset;
        if (asset is null || historyItem is null)
        {
            return;
        }

        try
        {
            IsBusy = true;
            var record = historyItem.SourceMaintenanceRecord;
            var activities = await _historyService.GetAuditTrailAsync(record.MaintenanceNumber).ConfigureAwait(true);
            var sourceWorkOrder = record.GeneratedByWorkOrderId.HasValue
                ? await _workOrderService.GetByIdAsync(record.GeneratedByWorkOrderId.Value).ConfigureAwait(true)
                : null;
            var printed = _printService.PrintMaintenanceRecord(record, activities, sourceWorkOrder);
            StatusText = printed
                ? _localization.Format("StatusHistoryItemPrintSentFormat", GetAssetDisplay(asset))
                : _localization.GetString("StatusPrintCancelled");
        }
        catch (Exception ex)
        {
            StatusText = _localization.GetString("StatusPrintFailed");
            _dialogService.ShowError(
                _localization.GetString("ErrorPrintFailedTitle"),
                GetExceptionMessage(ex));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task EnsureEntrySuggestionsLoadedAsync()
    {
        if (!_maintenanceSuggestionsLoaded)
        {
            _maintenanceEntrySuggestionCatalog = await _maintenanceRecordService.GetEntrySuggestionCatalogAsync().ConfigureAwait(true);
            _maintenanceSuggestionsLoaded = true;
        }
        if (!_workOrderSuggestionsLoaded)
        {
            _workOrderEntrySuggestionCatalog = await _workOrderService.GetEntrySuggestionCatalogAsync().ConfigureAwait(true);
            _workOrderSuggestionsLoaded = true;
        }
    }

    private void InvalidateEntrySuggestionCaches()
    {
        _maintenanceSuggestionsLoaded = false;
        _workOrderSuggestionsLoaded = false;
        _workOrderService.InvalidateSuggestionCache();
    }

    private MaintenanceEntryOptions BuildMaintenanceEntryOptions()
    {
        var sites = _maintenanceEntrySuggestionCatalog.Sites
            .Concat(_workOrderEntrySuggestionCatalog.Sites)
            .Concat(_maintenancePlans.Select(item => item.Site))
            .Concat(_assets.Select(item => item.Site));
        var locations = _maintenanceEntrySuggestionCatalog.Locations
            .Concat(_workOrderEntrySuggestionCatalog.Locations)
            .Concat(_maintenancePlans.Select(item => item.Location))
            .Concat(_assets.Select(item => item.Location));
        var subjects = _maintenanceEntrySuggestionCatalog.Subjects
            .Concat(_workOrderEntrySuggestionCatalog.Subjects)
            .Concat(_maintenancePlans.Select(item => item.PlanName))
            .Concat(_assets.Select(item => item.AssetName));
        var maintenanceTypes = MaintenanceTypeCatalog.BuiltIn.Concat(CustomMaintenanceTypes);
        return new MaintenanceEntryOptions(
            NormalizeSuggestions(sites),
            NormalizeSuggestions(locations),
            NormalizeSuggestions(subjects),
            NormalizeSuggestions(maintenanceTypes),
            _assets.ToArray(),
            IsAssetTrackingEnabled);
    }

    private static string[] NormalizeSuggestions(IEnumerable<string> values) => values
        .Select(item => item?.Trim() ?? string.Empty)
        .Where(item => item.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(item => item, StringComparer.CurrentCultureIgnoreCase)
        .ToArray();

    private static string GetApplicationVersion()
    {
        var version = typeof(MainViewModel).Assembly.GetName().Version;
        return version is null
            ? "2.2.0"
            : $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}";
    }

    private YearOption CreateAllYearsOption() =>
        new(null, _localization.GetString("YearAllYears"));

    private YearOption CreateYearOption(int year)
    {
        var yearText = year.ToString(_localization.CurrentCulture);
        return new YearOption(
            year,
            year == DateTime.Today.Year
                ? _localization.Format("YearCurrentFormat", yearText)
                : _localization.Format("YearArchivedFormat", yearText));
    }

    private string[] BuildMaintenanceSuggestions(Func<GlobalHistoryItemViewModel, string> selector) =>
        _globalHistory
            .Where(item => item.SourceMaintenanceRecord is not null)
            .Select(selector)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

    private IReadOnlyList<LanguageOption> CreateLanguageOptions() =>
    [
        new LanguageOption("en", _localization.GetString("LanguageEnglish")),
        new LanguageOption("ar", _localization.GetString("LanguageArabic"))
    ];

}
