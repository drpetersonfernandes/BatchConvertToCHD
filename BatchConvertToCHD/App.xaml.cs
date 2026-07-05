using System.IO;
using System.Windows;
using System.Windows.Threading;
using BatchConvertToCHD.Services;

namespace BatchConvertToCHD;

/// <summary>
/// Application class for BatchConvertToCHD. Handles startup, exception handling, and service initialization.
/// </summary>
public partial class App
{
    private Mutex? _singleInstanceMutex;
    private BugReportService? _bugReportService;
    private StatsService? _statsService;

    /// <summary>
    /// Provides a shared, static instance of the BugReportService for the entire application.
    /// </summary>
    public static BugReportService? SharedBugReportService { get; private set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="App"/> class.
    /// Sets up services, exception handling, and event handlers.
    /// </summary>
    public App()
    {
        // Initialize services
        SharedBugReportService = new BugReportService(AppConfig.BugReportApiUrl, AppConfig.BugReportApiKey, AppConfig.ApplicationName);
        _bugReportService = SharedBugReportService;

        _statsService = new StatsService(AppConfig.ApplicationStatsApiUrl, AppConfig.ApplicationStatsApiKey, AppConfig.ApplicationName);

        // Set up global exception handling
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

        // Register the Exit event handler
        Exit += App_Exit;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(true, $"Global\\{AppConfig.ApplicationName}_SingleInstance", out var createdNew);
        if (!createdNew)
        {
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;

            MessageBox.Show(
                $"Another instance of {AppConfig.ApplicationName} is already running.",
                AppConfig.ApplicationName,
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            Shutdown();
            return;
        }

        // Set shutdown mode to close the application when the main window closes
        // This ensures the app fully terminates when the user closes the window
        ShutdownMode = ShutdownMode.OnMainWindowClose;

        // Delete old 7z DLL files if they exist
        DeleteOldDllFiles();

        base.OnStartup(e);

        // Record usage statistics on a background thread
        _ = _statsService?.RecordUsageAsync();

        // Preload assemblies on background thread to improve responsiveness
        Task.Run(static () =>
        {
            try
            {
                var assemblies = AppDomain.CurrentDomain.GetAssemblies();
                foreach (var assembly in assemblies)
                {
                    try
                    {
                        assembly.GetTypes();
                    }
                    catch
                    {
                        // ignored
                    }
                }
            }
            catch
            {
                // ignored
            }
        });
    }

    private static void DeleteOldDllFiles()
    {
        try
        {
            string[] dllFilesToDelete = ["7z_x64.dll", "7z_arm64.dll"];
            var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;

            foreach (var dllFile in dllFilesToDelete)
            {
                var filePath = Path.Combine(baseDirectory, dllFile);
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
            }
        }
        catch
        {
            // Silently ignore errors when deleting old DLL files
        }
    }

    private void App_Exit(object sender, ExitEventArgs e)
    {
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;

        AppHttpClient.Dispose();

        _bugReportService = null;
        SharedBugReportService = null;
        _statsService = null;

        AppDomain.CurrentDomain.UnhandledException -= CurrentDomain_UnhandledException;
        DispatcherUnhandledException -= App_DispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException -= TaskScheduler_UnobservedTaskException;
    }

    private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            ReportException(exception, "AppDomain.UnhandledException");
        }
    }

    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Suppress WPF internal font rendering errors (UriFormatException from GlyphTypeface)
        // These are caused by system fonts with invalid paths and are not actionable by us
        if (e.Exception is UriFormatException uriEx && uriEx.StackTrace?.Contains("GlyphTypeface") == true)
        {
            e.Handled = true;
            return;
        }

        ReportException(e.Exception, "Application.DispatcherUnhandledException");
        e.Handled = true;
    }

    private void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        ReportException(e.Exception, "TaskScheduler.UnobservedTaskException");
        e.SetObserved();
    }

    private void ReportException(Exception exception, string source)
    {
        try
        {
            // Notify the developer using the shared service instance
            // Block synchronously — critical for AppDomain.UnhandledException where the OS
            // terminates the process immediately after this handler returns. Using async void
            // would fire off the HTTP request and return before it completes, losing the report.
            _bugReportService?.SendBugReportAsync($"Unhandled Exception from {source}", exception).GetAwaiter().GetResult();
        }
        catch
        {
            // Silently ignore any errors in the reporting process
        }
    }
}
