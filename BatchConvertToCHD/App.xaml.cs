using System.Windows;
using System.Windows.Threading;
using BatchConvertToCHD.Services;

namespace BatchConvertToCHD;

public partial class App : IDisposable
{
    private BugReportService? _bugReportService;

    /// <summary>
    /// Provides a shared, static instance of the BugReportService for the entire application.
    /// </summary>
    public static BugReportService? SharedBugReportService { get; private set; }

    public App()
    {
        // Initialize the bug report service first to ensure it's available for reporting initialization errors
        SharedBugReportService = new BugReportService(AppConfig.BugReportApiUrl, AppConfig.BugReportApiKey, AppConfig.ApplicationName);
        _bugReportService = SharedBugReportService;

        // Set up global exception handling
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

        // Register the Exit event handler
        Exit += App_Exit;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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

    private void App_Exit(object sender, ExitEventArgs e)
    {
        // Dispose the bug report service and clear references to prevent double disposal
        // if Dispose() is called explicitly later
        _bugReportService?.Dispose();
        _bugReportService = null;
        SharedBugReportService = null;

        // Unregister static event handlers to prevent memory leaks
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
        ReportException(e.Exception, "Application.DispatcherUnhandledException");
        e.Handled = true;
    }

    private void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        ReportException(e.Exception, "TaskScheduler.UnobservedTaskException");
        e.SetObserved();
    }

    private async void ReportException(Exception exception, string source)
    {
        try
        {
            // Notify the developer using the shared service instance
            if (_bugReportService != null)
            {
                await _bugReportService.SendBugReportAsync($"Unhandled Exception from {source}", exception);
            }
        }
        catch
        {
            // Silently ignore any errors in the reporting process
        }
    }

    /// <inheritdoc />
    /// <summary>
    /// Releases all resources used by the App.
    /// </summary>
    public void Dispose()
    {
        // Cleanup is primarily handled in App_Exit. This method provides a safety net
        // for explicit disposal scenarios and prevents double disposal.
        if (_bugReportService != null)
        {
            _bugReportService.Dispose();
            _bugReportService = null;
            SharedBugReportService = null;
        }

        // Unregister static event handlers to prevent them from firing after disposal
        AppDomain.CurrentDomain.UnhandledException -= CurrentDomain_UnhandledException;
        DispatcherUnhandledException -= App_DispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException -= TaskScheduler_UnobservedTaskException;

        // Suppress finalization
        GC.SuppressFinalize(this);
    }
}
