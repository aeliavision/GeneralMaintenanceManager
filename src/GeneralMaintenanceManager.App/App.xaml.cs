using System.Windows;
using System.Windows.Threading;
using GeneralMaintenanceManager.App.Services;
using GeneralMaintenanceManager.App.ViewModels;
using GeneralMaintenanceManager.App.Views;
using GeneralMaintenanceManager.Core.Abstractions;
using GeneralMaintenanceManager.Infrastructure.Data;
using GeneralMaintenanceManager.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace GeneralMaintenanceManager.App;

public partial class App : Application, IDisposable
{
    private readonly IHost _host;
    private readonly CancellationTokenSource _applicationLifetime = new();
    private SingleInstanceGuard? _singleInstanceGuard;

    public App()
    {
        var builder = Host.CreateApplicationBuilder();

        builder.Services.AddSingleton<DataPaths>();
        builder.Services.AddSingleton<DatabaseContextFactory>();
        builder.Services.AddSingleton<DatabaseInitializer>();
        builder.Services.AddSingleton<SqliteMaintenanceService>();
        builder.Services.AddSingleton<ProductionMigrationCoordinator>();
        builder.Services.AddSingleton<AnnualMaintenanceDatabaseManager>();
        builder.Services.AddSingleton<SingleInstanceGuard>();
        builder.Services.AddSingleton<DatabaseActivityGate>();
        builder.Services.AddSingleton<StartupPerformanceLogger>();

        builder.Services.AddSingleton<IDataLocationService>(services => services.GetRequiredService<DataPaths>());
        builder.Services.AddSingleton<IAssetService, AssetService>();
        builder.Services.AddSingleton<IMaintenanceHistoryService, MaintenanceHistoryService>();
        builder.Services.AddSingleton<IMaintenanceRecordService, MaintenanceRecordService>();
        builder.Services.AddSingleton<IActivityHistoryService, ActivityHistoryService>();
        builder.Services.AddSingleton<IGlobalHistoryQueryService, GlobalHistoryQueryService>();
        builder.Services.AddSingleton<IExcelImportService, ExcelImportService>();
        builder.Services.AddSingleton<IAssetExportService, AssetExportService>();
        builder.Services.AddSingleton<IDataHealthService, DatabaseHealthService>();
        builder.Services.AddSingleton<IDatabaseMaintenanceService, DatabaseMaintenanceService>();
        builder.Services.AddSingleton<IDiagnosticLogger, DiagnosticLogger>();
        builder.Services.AddSingleton<IBackupService, BackupService>();
        builder.Services.AddSingleton<IReviewService, ReviewService>();
        builder.Services.AddSingleton<IWorkOrderService, WorkOrderService>();
        builder.Services.AddSingleton<IMaintenancePlanService, MaintenancePlanService>();

        builder.Services.AddSingleton<ILocalizationService, LocalizationService>();
        builder.Services.AddSingleton<IUserSettingsService, UserSettingsService>();
        builder.Services.AddSingleton<IDialogService, DialogService>();
        builder.Services.AddSingleton<IPrintService, PrintService>();
        builder.Services.AddSingleton<MainViewModel>();
        builder.Services.AddSingleton<MainWindow>();

        _host = builder.Build();
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Any(arg => string.Equals(arg, "--ui-smoke-test", StringComparison.OrdinalIgnoreCase)))
        {
            await RunUiSmokeTestAsync().ConfigureAwait(true);
            return;
        }

        SplashWindow? splash = null;
        ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;

        try
        {
            splash = new SplashWindow();
            splash.Show();
            await Dispatcher.Yield(DispatcherPriority.Render);

            splash.SetProgress(8, GetStartupText("SplashStarting"));
            await _host.StartAsync(_applicationLifetime.Token).ConfigureAwait(true);
            var startupLog = _host.Services.GetRequiredService<StartupPerformanceLogger>();
            startupLog.LogStage("Host");

            _host.Services.GetRequiredService<IDiagnosticLogger>().Info(
                "Startup",
                $"General Maintenance Manager {typeof(App).Assembly.GetName().Version} starting from {AppContext.BaseDirectory}.");

            var localization = _host.Services.GetRequiredService<ILocalizationService>();
            var userSettings = _host.Services.GetRequiredService<IUserSettingsService>();
            localization.SetLanguage(userSettings.LoadLanguageCode());
            splash.SetProgress(16, localization.GetString("SplashLoadingPreferences"));
            startupLog.LogStage("Preferences");

            _singleInstanceGuard = _host.Services.GetRequiredService<SingleInstanceGuard>();
            if (!_singleInstanceGuard.TryAcquire())
            {
                splash.Close();
                splash = null;
                ModernMessageDialog.ShowError(
                    null,
                    localization.GetString("SecondInstanceTitle"),
                    localization.GetString("SecondInstanceMessage"),
                    localization.CurrentCulture);
                Shutdown(-2);
                return;
            }
            startupLog.LogStage("Single instance guard");

            // O(1) startup contract: initialize only master + current-year physical schemas.
            // Archived years are discovered by filename and opened lazily when requested.
            var initializer = _host.Services.GetRequiredService<DatabaseInitializer>();
            splash.SetProgress(34, localization.GetString("SplashPreparingData"));
            await initializer.InitializeAsync().ConfigureAwait(true);
            startupLog.LogStage("Initialize master/current year");

            // Startup health is identity/connectivity only. PRAGMA quick_check, ANALYZE,
            // FTS rebuilds and full cross-year integrity are explicit maintenance actions.
            var health = _host.Services.GetRequiredService<IDataHealthService>();
            splash.SetProgress(50, localization.GetString("SplashCheckingData"));
            await health.ValidateStartupDataAsync().ConfigureAwait(true);
            startupLog.LogStage("Light startup validation");

            // Only replay the durable cross-database queue. When empty this is a tiny indexed read.
            splash.SetProgress(63, localization.GetString("SplashRecoveringChanges"));
            var workOrders = _host.Services.GetRequiredService<IWorkOrderService>();
            await workOrders.RecoverPendingCompletionsAsync().ConfigureAwait(true);
            startupLog.LogStage("Pending work-order completion recovery");

            splash.SetProgress(74, localization.GetString("SplashPreparingWorkspace"));
            var viewModel = _host.Services.GetRequiredService<MainViewModel>();
            splash.SetProgress(88, localization.GetString("SplashLoadingWorkspace"));
            await viewModel.InitializeAsync().ConfigureAwait(true);
            startupLog.LogStage("Initial bounded workspace");

            var mainWindow = _host.Services.GetRequiredService<MainWindow>();
            MainWindow = mainWindow;
            ShutdownMode = System.Windows.ShutdownMode.OnMainWindowClose;

            splash.SetProgress(100, localization.GetString("SplashReady"));
            await Dispatcher.Yield(DispatcherPriority.Render);
            mainWindow.Show();
            startupLog.LogStage("Main window shown");
            splash.Close();
            splash = null;

            // Historical detail is intentionally post-first-paint.
            await viewModel.LoadInitialHistoryAsync().ConfigureAwait(true);
            startupLog.LogStage("Post-paint initial history");
        }
        catch (Exception ex)
        {
            try
            {
                _host.Services.GetService<IDiagnosticLogger>()?.LogError("Startup", ex, "Application startup failed.");
            }
            catch
            {
                // Diagnostics are best-effort and must never mask the original startup failure.
            }
            splash?.Close();

            var localization = _host.Services.GetService<ILocalizationService>();
            var title = localization?.GetString("AppTitle")
                ?? TryFindResource("AppTitle") as string
                ?? string.Empty;

            var startupFormat = TryFindResource("StartupErrorFormat") as string ?? "{0}";
            var message = localization?.Format("StartupErrorFormat", ex.Message)
                ?? string.Format(System.Globalization.CultureInfo.CurrentCulture, startupFormat, ex.Message);

            ModernMessageDialog.ShowError(
                null,
                title,
                message,
                localization?.CurrentCulture ?? System.Globalization.CultureInfo.CurrentUICulture);

            Shutdown(-1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _applicationLifetime.Cancel();
            _host.StopAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
        }
        finally
        {
            Dispose();
            base.OnExit(e);
        }
    }

    public void Dispose()
    {
        _singleInstanceGuard?.Dispose();
        _singleInstanceGuard = null;
        _applicationLifetime.Dispose();
        _host.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task RunUiSmokeTestAsync()
    {
        ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
        try
        {
            await _host.StartAsync(_applicationLifetime.Token).ConfigureAwait(true);
            var startupLog = _host.Services.GetRequiredService<StartupPerformanceLogger>();
            startupLog.LogStage("Smoke host");

            var localization = _host.Services.GetRequiredService<ILocalizationService>();
            var userSettings = _host.Services.GetRequiredService<IUserSettingsService>();
            localization.SetLanguage(userSettings.LoadLanguageCode());

            _singleInstanceGuard = _host.Services.GetRequiredService<SingleInstanceGuard>();
            if (!_singleInstanceGuard.TryAcquire())
            {
                Shutdown(-2);
                return;
            }

            var initializer = _host.Services.GetRequiredService<DatabaseInitializer>();
            await initializer.InitializeAsync(_applicationLifetime.Token).ConfigureAwait(true);
            startupLog.LogStage("Smoke database initialize");

            var health = _host.Services.GetRequiredService<IDataHealthService>();
            await health.ValidateStartupDataAsync(_applicationLifetime.Token).ConfigureAwait(true);
            startupLog.LogStage("Smoke startup health");

            var workOrders = _host.Services.GetRequiredService<IWorkOrderService>();
            await workOrders.RecoverPendingCompletionsAsync(_applicationLifetime.Token).ConfigureAwait(true);
            startupLog.LogStage("Smoke recovery");

            // Resolving the real WPF window constructs its XAML, converters, bindings and view-model graph.
            _ = _host.Services.GetRequiredService<MainWindow>();
            startupLog.LogStage("Smoke MainWindow constructed");
            Shutdown(0);
        }
        catch (Exception ex)
        {
            try { _host.Services.GetService<IDiagnosticLogger>()?.LogError("UiSmokeTest", ex, "Packaged UI smoke test failed."); }
            catch { }
            Shutdown(-3);
        }
    }

    private string GetStartupText(string resourceKey) =>
        TryFindResource(resourceKey) as string ?? resourceKey;
}
