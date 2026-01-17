using System.IO;
using System.Windows;
using System.Windows.Threading;
using BatchConvertToCHD.Services;
using SevenZip;

namespace BatchConvertToCHD;

public partial class App : IDisposable
{
    private readonly BugReportService? _bugReportService;

    /// <summary>
    /// Provides a shared, static instance of the BugReportService for the entire application.
    /// </summary>
    public static BugReportService? SharedBugReportService { get; private set; }

    /// <summary>
    /// Indicates whether the 7-Zip library (7z_x64.dll) is available.
    /// </summary>
    public static bool IsSevenZipAvailable { get; private set; }

    public App()
    {
        // Initialize SevenZipSharp library path first to determine its availability
        InitializeSevenZipSharp();

        // Initialize the bug report service as a shared instance
        SharedBugReportService = new BugReportService(AppConfig.BugReportApiUrl, AppConfig.BugReportApiKey, AppConfig.ApplicationName, IsSevenZipAvailable);
        _bugReportService = SharedBugReportService;

        // Set up global exception handling
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

        // Register the Exit event handler
        Exit += App_Exit;
    }

    private void App_Exit(object sender, ExitEventArgs e)
    {
        _bugReportService?.Dispose();

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

    private void InitializeSevenZipSharp()
    {
        try
        {
            const string dllName = "7z_x64.dll";
            var dllPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, dllName);

            if (File.Exists(dllPath))
            {
                SevenZipBase.SetLibraryPath(dllPath);
                IsSevenZipAvailable = true;
            }
            else
            {
                // Notify developer
                var errorMessage = $"Could not find the required 7-Zip library: {dllName} in {AppDomain.CurrentDomain.BaseDirectory}";
                if (_bugReportService != null)
                {
                    _ = _bugReportService.SendBugReportAsync(errorMessage, null);
                }

                IsSevenZipAvailable = false;
            }
        }
        catch (Exception ex)
        {
            // Notify developer
            if (_bugReportService != null)
            {
                _ = _bugReportService.SendBugReportAsync("Error initializing 7-Zip library", ex);
            }

            IsSevenZipAvailable = false;
        }
    }

    /// <inheritdoc />
    /// <summary>
    /// Releases all resources used by the App.
    /// </summary>
    public void Dispose()
    {
        // Dispose of the shared BugReportService instance
        _bugReportService?.Dispose();
        SharedBugReportService = null;

        // Unregister event handlers to prevent memory leaks
        AppDomain.CurrentDomain.UnhandledException -= CurrentDomain_UnhandledException;
        DispatcherUnhandledException -= App_DispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException -= TaskScheduler_UnobservedTaskException;

        // Suppress finalization
        GC.SuppressFinalize(this);
    }
}