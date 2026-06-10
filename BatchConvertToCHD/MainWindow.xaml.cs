using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using BatchConvertToCHD.Models;
using BatchConvertToCHD.Services;
using BatchConvertToCHD.Utilities;

namespace BatchConvertToCHD;

/// <summary>
/// Main application window for BatchConvertToCHD.
/// Provides functionality for converting, verifying, and extracting CHD files.
/// </summary>
public partial class MainWindow : IDisposable
{
    private CancellationTokenSource _cts;
    private readonly object _ctsLock = new();
    private readonly bool _isMaxCsoAvailable;
    private readonly bool _isChdmanAvailable;

    private readonly string _psxPackagerPath;
    private readonly bool _isPsxPackagerAvailable;
    private readonly bool _isSevenZipAvailable;

    // Statistics
    private int _totalFilesProcessed;
    private int _processedOkCount;
    private int _failedCount;
    private readonly Stopwatch _operationTimer = new();

    // Operation state tracking (0 = idle, >0 = running) - using Interlocked for thread safety
    private int _operationRunningState;

    // Tracks whether a close was requested while an operation was running
    private bool _pendingClose;

    // Services
    private readonly UpdateService _updateService;
    private readonly ArchiveService _archiveService;

    // Temp Directory Prefix
    private const string TempDirPrefix = "BatchConvertToCHD_Temp_";

    // File collections for DataGrids
    private readonly ObservableCollection<FileItem> _conversionFiles = new();
    private readonly ObservableCollection<FileItem> _verificationFiles = new();
    private readonly ObservableCollection<FileItem> _extractionFiles = new();

    // Performance counter for write speed monitoring
    private const int MaxLogLength = 100000; // Maximum characters before log truncation
    private readonly PerformanceCounter? _writeBytesCounter;
    private readonly PerformanceCounter? _readBytesCounter;
    private readonly object _performanceCounterLock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="MainWindow"/> class.
    /// Sets up services, checks for required executables, and initializes the UI.
    /// </summary>
    public MainWindow()
    {
        InitializeComponent();
        _cts = new CancellationTokenSource();

        ConversionFilesDataGrid.ItemsSource = _conversionFiles;
        VerificationFilesDataGrid.ItemsSource = _verificationFiles;
        ExtractionFilesDataGrid.ItemsSource = _extractionFiles;

        var appDirectory = AppDomain.CurrentDomain.BaseDirectory;
        var chdmanPath = Path.Combine(appDirectory, AppConfig.ChdmanExeName);
        _isChdmanAvailable = File.Exists(chdmanPath);

        var maxCsoPath = Path.Combine(appDirectory, "maxcso.exe");
        _isMaxCsoAvailable = File.Exists(maxCsoPath) && !AppConfig.IsArm64;

        _psxPackagerPath = Path.Combine(appDirectory, AppConfig.PsxPackagerExeName);
        _isPsxPackagerAvailable = File.Exists(_psxPackagerPath);

        var sevenZipExePath = Path.Combine(appDirectory, AppConfig.SevenZipExeName);
        _isSevenZipAvailable = File.Exists(sevenZipExePath);

        // Initialize Services
        _updateService = new UpdateService(AppConfig.ApplicationName);
        _archiveService = new ArchiveService(maxCsoPath, _isMaxCsoAvailable, sevenZipExePath, _isSevenZipAvailable);

        // Initialize performance counters
        _writeBytesCounter = CreateWritePerformanceCounter();
        _readBytesCounter = CreateReadPerformanceCounter();

        InitializeStatusBar();
        Task.Run(static async () =>
        {
            try
            {
                await Task.Delay(2000);
                CleanupLeftoverTempDirectories();
            }
            catch
            {
                /* ignore */
            }
        });
        DisplayConversionInstructionsInLog();
        ResetOperationStats();
        LogEnvironmentDetails();

        // Defer heavy initialization until after window is shown
        Loaded += MainWindow_LoadedAsync;

        // Hide speed display initially until we know counters are available
        SpeedStatCard.Visibility = Visibility.Collapsed;
    }

    private async void MainWindow_LoadedAsync(object sender, RoutedEventArgs e)
    {
        try
        {
            // Apply command-line argument for input folder path if provided
            var args = Environment.GetCommandLineArgs();
            if (args.Length > 1)
            {
                var inputPath = args[1];
                SetInputFolder(inputPath);
            }

            // Show speed display if counters are available
            if (_writeBytesCounter != null || _readBytesCounter != null)
            {
                SpeedStatCard.Visibility = Visibility.Visible;
            }

            // Check for missing dependencies and notify user
            CheckDependenciesAndNotifyUser();

            // Defer update check until window is responsive
            await Task.Delay(100); // Allow UI to render first
            _ = _updateService.CheckForNewVersionAsync(LogMessage, UpdateStatusBarMessage, ReportBugAsync);
        }
        catch (Exception ex)
        {
            _ = ReportBugAsync("MainWindow_Loaded error", ex);
        }
    }

    private void CheckDependenciesAndNotifyUser()
    {
        var missingDeps = new List<string>();
        if (!_isChdmanAvailable)
        {
            missingDeps.Add(AppConfig.ChdmanExeName);
        }

        // Critical dependency check
        if (missingDeps.Count > 0)
        {
            var msg = $"CRITICAL ERROR: The following required component is missing:\n\n" +
                      $"{string.Join("\n", missingDeps)}\n\n" +
                      $"Please ensure it is placed in the application folder.\n" +
                      $"Conversion, Verification and Extraction will NOT work without it.";

            LogMessage("!!! CRITICAL ERROR: " + msg.Replace("\n", " "));
            ShowMessageBox(msg, "Missing Dependency", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        // Optional but recommended dependencies
        var missingOptional = new List<string>();
        if (!_isMaxCsoAvailable && !AppConfig.IsArm64)
        {
            missingOptional.Add("maxcso.exe (required for .cso files)");
        }

        if (!_isPsxPackagerAvailable)
        {
            missingOptional.Add(AppConfig.PsxPackagerExeName + " (required for .pbp files)");
        }

        if (missingOptional.Count > 0)
        {
            LogMessage("NOTE: Some optional components are missing: " + string.Join(", ", missingOptional));
        }
    }

    private static PerformanceCounter? CreateWritePerformanceCounter()
    {
        try
        {
            // Check if category exists first to avoid registry errors
            if (!PerformanceCounterCategory.Exists("PhysicalDisk"))
            {
                return null;
            }

            // Create a performance counter for disk write operations
            return new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", "_Total");
        }
        catch (InvalidOperationException)
        {
            // System configuration issue - counters unavailable
            return null;
        }
        catch
        {
            // Best effort - return null if creation fails
            return null;
        }
    }

    private static PerformanceCounter? CreateReadPerformanceCounter()
    {
        try
        {
            // Check if category exists first to avoid registry errors
            if (!PerformanceCounterCategory.Exists("PhysicalDisk"))
            {
                return null;
            }

            // Create a performance counter for disk read operations
            return new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", "_Total");
        }
        catch (InvalidOperationException)
        {
            // System configuration issue - counters unavailable
            return null;
        }
        catch
        {
            // Best effort - return null if creation fails
            return null;
        }
    }

    private void InitializeStatusBar()
    {
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            StatusBarChdman.Text = " CHDMAN ";
            StatusBarChdman.Foreground = _isChdmanAvailable
                ? (System.Windows.Media.Brush?)Application.Current.FindResource("SuccessTextBrush") ?? System.Windows.Media.Brushes.Gray
                : (System.Windows.Media.Brush?)Application.Current.FindResource("FailedTextBrush") ?? System.Windows.Media.Brushes.Gray;
            StatusBarMaxcso.Text = " MAXCSO ";
            StatusBarMaxcso.Foreground = _isMaxCsoAvailable
                ? (System.Windows.Media.Brush?)Application.Current.FindResource("SuccessTextBrush") ?? System.Windows.Media.Brushes.Gray
                : (System.Windows.Media.Brush?)Application.Current.FindResource("SkippedTextBrush") ?? System.Windows.Media.Brushes.Gray;
            StatusBarPsxPackager.Text = " PSXPACKAGER ";
            StatusBarPsxPackager.Foreground = _isPsxPackagerAvailable
                ? (System.Windows.Media.Brush?)Application.Current.FindResource("SuccessTextBrush") ?? System.Windows.Media.Brushes.Gray
                : (System.Windows.Media.Brush?)Application.Current.FindResource("SkippedTextBrush") ?? System.Windows.Media.Brushes.Gray;
            StatusBarMessage.Text = "Ready";
            SpeedValue.Text = "0.0 MB/s";
        });
    }

    private static void CleanupLeftoverTempDirectories()
    {
        Task.Run(static () =>
        {
            try
            {
                foreach (var basePath in PathUtils.GetPossibleTempBasePaths())
                {
                    try
                    {
                        var directories = Directory.GetDirectories(basePath, $"{TempDirPrefix}*");
                        foreach (var dir in directories)
                        {
                            try
                            {
                                Directory.Delete(dir, true);
                            }
                            catch
                            {
                                /* ignore */
                            }
                        }
                    }
                    catch
                    {
                        /* ignore */
                    }
                }
            }
            catch
            {
                /* ignore */
            }
        });
    }

    private void UpdateStatusBarMessage(string message)
    {
        Application.Current.Dispatcher.InvokeAsync(() => StatusBarMessage.Text = message);
    }

    private async Task<bool> ValidateExecutableAccessAsync(string exePath, string exeName)
    {
        try
        {
            if (!File.Exists(exePath))
            {
                LogMessage($"ERROR: {exeName} not found at: {exePath}");
                ShowError($"{exeName} not found.");
                return false;
            }

            // Check if file has executable extension
            if (!exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                LogMessage($"ERROR: {exeName} is not an executable file.");
                ShowError($"{exeName} is not a valid executable.");
                return false;
            }

            // Check for read access and file locks by attempting to open with exclusive access
            try
            {
                await using (File.Open(exePath, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    // File is not locked and we have read access
                }
            }
            catch (IOException ioEx) when (ioEx.Message.Contains("being used by another process", StringComparison.OrdinalIgnoreCase))
            {
                LogMessage($"ERROR: {exeName} is locked by another process.");
                ShowError($"{exeName} is currently in use by another process.");
                return false;
            }

            // Check for execution permissions by verifying file attributes
            var fileInfo = new FileInfo(exePath);
            if (fileInfo.Attributes.HasFlag(FileAttributes.ReadOnly) && !IsRunningAsAdmin())
            {
                // Read-only files can still be executed, but log a warning
                LogMessage($"WARNING: {exeName} is read-only.");
            }

            return true;
        }
        catch (UnauthorizedAccessException)
        {
            LogMessage($"PERMISSION ERROR: Cannot access {exeName}. Insufficient permissions.");
            ShowError($"Access denied to {exeName}. Check antivirus or permissions.");
            return false;
        }
        catch (Exception ex)
        {
            LogMessage($"ERROR: Cannot access {exeName}. {ex.Message}");
            ShowError($"Cannot access {exeName}. Check antivirus or permissions.");
            _ = ReportBugAsync($"Cannot access {exeName}", ex);
            return false;
        }
    }

    /// <summary>
    /// Checks if the application is running with administrator privileges.
    /// </summary>
    private static bool IsRunningAsAdmin()
    {
        try
        {
            var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Validates that chdman.exe is compatible with the current OS platform.
    /// This catches Win32Exception (0x800700C1) when the executable is not valid for this OS.
    /// </summary>
    private async Task<bool> ValidateChdmanCompatibilityAsync(string chdmanPath, CancellationToken token)
    {
        using var process = new Process();
        try
        {
            process.StartInfo = new ProcessStartInfo
            {
                FileName = chdmanPath,
                Arguments = "help",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                ErrorDialog = false
            };

            process.Start();
            await process.WaitForExitAsync(token);
            return true;
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(true);
                    await Task.Run(() => process.WaitForExit(5000), CancellationToken.None);
                }
                catch
                {
                    // Best effort - ignore errors during cleanup
                }
            }

            throw;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 193 || ex.Message.Contains("not a valid application"))
        {
            LogMessage("ERROR: The bundled chdman.exe is not compatible with this version of Windows.");
            LogMessage("       This typically occurs when running on older Windows versions (e.g., Windows 7).");
            LogMessage("       Please download a compatible version of chdman.exe from MAME releases.");
            ShowError("chdman.exe is not compatible with this OS.\n\n" +
                      "The bundled chdman.exe requires a newer Windows version.\n" +
                      "For Windows 7, please obtain a compatible chdman.exe from an older MAME release.");
            return false;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 5)
        {
            LogMessage($"ERROR: Access denied when trying to start {Path.GetFileName(chdmanPath)}.");
            LogMessage("       This can be caused by antivirus blocking the executable or insufficient file permissions.");
            ShowError($"Access denied to {Path.GetFileName(chdmanPath)}.\n\nPlease check your antivirus settings or file permissions.");
            return false;
        }
        catch (Exception ex)
        {
            // Ensure process is terminated on any other exception
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(true);
                    await Task.Run(() => process.WaitForExit(5000), CancellationToken.None);
                }
                catch
                {
                    // Best effort - ignore errors during cleanup
                }
            }

            // Other errors are acceptable - at least the exe started or we have a generic error
            LogMessage($"WARNING: Could not validate chdman compatibility: {ex.Message}");
            _ = ReportBugAsync("Could not validate chdman compatibility", ex);
            return true;
        }
    }

    private void LogEnvironmentDetails()
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== Environment Details ===");
            sb.AppendLine(CultureInfo.InvariantCulture, $"OS: {Environment.OSVersion}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"User: {Environment.UserName}");
            LogMessage(sb.ToString());
        }
        catch
        {
            /* ignore */
        }
    }

    private void DisplayConversionInstructionsInLog()
    {
        LogMessage($"Welcome to {AppConfig.ApplicationName}. (Conversion Mode)");
        if (!_isChdmanAvailable)
        {
            LogMessage("WARNING: chdman.exe not found!");
        }

        if (!_isMaxCsoAvailable)
        {
            LogMessage("WARNING: maxcso.exe not found.");
        }

        if (!_isPsxPackagerAvailable)
        {
            LogMessage("WARNING: psxpackager.exe not found. PBP conversion unavailable.");
        }

        LogMessage("--- Ready for Conversion ---");
    }

    private void DisplayVerificationInstructionsInLog()
    {
        LogMessage($"Welcome to {AppConfig.ApplicationName}. (Verification Mode)");
        if (!_isChdmanAvailable)
        {
            LogMessage("WARNING: chdman.exe not found!");
        }

        LogMessage("--- Ready for Verification ---");
    }

    private void DisplayExtractionInstructionsInLog()
    {
        LogMessage($"Welcome to {AppConfig.ApplicationName}. (Extraction Mode)");
        if (!_isChdmanAvailable)
        {
            LogMessage("WARNING: chdman.exe not found!");
        }

        LogMessage("This feature extracts CHD files back to their original format (ISO/BIN/CUE etc.)");
        LogMessage("--- Ready for Extraction ---");
    }

    private void MainTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source is not TabControl control)
        {
            return;
        }

        if (!StartConversionButton.IsEnabled && !StartVerificationButton.IsEnabled)
        {
            return;
        }

        Application.Current.Dispatcher.InvokeAsync((Action)(() => LogViewer.Clear()));
        if (control.SelectedItem is TabItem selectedTab)
        {
            switch (selectedTab.Name)
            {
                case "ConvertTab":
                    DisplayConversionInstructionsInLog();
                    UpdateStatusBarMessage("Ready for conversion");
                    SpeedValue.Text = "0.0 MB/s";
                    break;
                case "VerifyTab":
                    DisplayVerificationInstructionsInLog();
                    UpdateStatusBarMessage("Ready for verification");
                    break;
                case "ExtractTab":
                    DisplayExtractionInstructionsInLog();
                    UpdateStatusBarMessage("Ready for extraction");
                    break;
            }
        }

        UpdateWriteSpeedDisplay(0);
        UpdateReadSpeedDisplay(0);
    }

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        // Check if any operation is currently running using thread-safe Interlocked check
        var isOperationRunning = Interlocked.CompareExchange(ref _operationRunningState, 0, 0) != 0;

        if (isOperationRunning)
        {
            lock (_ctsLock)
            {
                if (!_cts.IsCancellationRequested)
                {
                    _cts.Cancel();
                    _pendingClose = true;
                    LogMessage("Cancelling operations before closing...");
                    UpdateStatusBarMessage("Cancelling...");
                    e.Cancel = true;
                    return;
                }
            }
        }

        Dispose();

        Application.Current.Shutdown();
    }

    private void LogMessage(string message)
    {
        var timestampedMessage = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";

        Application.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                // Optimization: Don't get the whole LogViewer.Text (slow for large strings)
                // Use Selection properties for smarter truncation if possible,
                // but at minimum, check length directly
                if (LogViewer.Text.Length > MaxLogLength)
                {
                    var excess = LogViewer.Text.Length - MaxLogLength / 2;
                    LogViewer.SelectionStart = 0;
                    LogViewer.SelectionLength = excess;
                    LogViewer.SelectedText = $"[{DateTime.Now:HH:mm:ss.fff}] --- Log truncated to keep app responsive ---{Environment.NewLine}";
                }

                LogViewer.AppendText($"{timestampedMessage}{Environment.NewLine}");
                LogViewer.ScrollToEnd();
            }
            catch
            {
                /* ignore logging errors */
            }
        }));
    }

    /// <summary>
    /// Sets the input folder for conversion from a command line argument.
    /// </summary>
    /// <param name="path">The path to the input folder.</param>
    public void SetInputFolder(string path)
    {
        if (Directory.Exists(path))
        {
            ConversionInputFolderTextBox.Text = path;
            LogMessage($"Input folder set from command line: {path}");
            _ = LoadFilesForConversionAsync();
        }
        else
        {
            LogMessage($"Warning: Command line path does not exist: {path}");
        }
    }

    private void BrowseConversionInputButton_Click(object sender, RoutedEventArgs e)
    {
        HandleFolderBrowse(ConversionInputFolderTextBox, "Conversion input");
    }

    private void BrowseConversionOutputButton_Click(object sender, RoutedEventArgs e)
    {
        HandleFolderBrowse(ConversionOutputFolderTextBox, "Conversion output");
    }

    private void BrowseVerificationInputButton_Click(object sender, RoutedEventArgs e)
    {
        HandleFolderBrowse(VerificationInputFolderTextBox, "Verification input");
    }

    private void BrowseExtractionInputButton_Click(object sender, RoutedEventArgs e)
    {
        HandleFolderBrowse(ExtractionInputFolderTextBox, "Extraction input");
    }

    private void BrowseExtractionOutputButton_Click(object sender, RoutedEventArgs e)
    {
        HandleFolderBrowse(ExtractionOutputFolderTextBox, "Extraction output");
    }

    private async void StartExtractionButton_ClickAsync(object sender, RoutedEventArgs e)
    {
        try
        {
            await Application.Current.Dispatcher.InvokeAsync((Action)(() => LogViewer.Clear()));
            DisplayExtractionInstructionsInLog();

            if (!_isChdmanAvailable)
            {
                ShowError($"{AppConfig.ChdmanExeName} is missing.");
                return;
            }

            var inputFolder = PathUtils.ValidateAndNormalizePath(ExtractionInputFolderTextBox.Text, "CHD Files Folder", ShowError, LogMessage);
            var outputFolder = PathUtils.ValidateAndNormalizePath(ExtractionOutputFolderTextBox.Text, "Output Folder", ShowError, LogMessage);

            if (inputFolder == null || outputFolder == null)
            {
                return;
            }

            if (!Directory.Exists(inputFolder))
            {
                ShowError($"Input folder does not exist: {inputFolder}");
                return;
            }

            if (!Directory.Exists(outputFolder))
            {
                ShowError($"Output folder does not exist: {outputFolder}");
                return;
            }

            if (inputFolder.Equals(outputFolder, StringComparison.OrdinalIgnoreCase))
            {
                ShowError("Input and output folders must be different.");
                return;
            }

            var selectedFiles = _extractionFiles.Where(static f => f.IsSelected).Select(static f => f.FullPath).ToArray();
            if (selectedFiles.Length == 0)
            {
                ShowError("No files selected for extraction.");
                return;
            }

            RenewCancellationTokenSource();

            ResetOperationStats();
            SetControlsState(false);
            await Task.Yield();
            _operationTimer.Restart();
            ResetSpeedCounters();

            var deleteOriginal = DeleteOriginalChdCheckBox.IsChecked ?? false;

            LogMessage("--- Starting batch extraction process... ---");

            try
            {
                CancellationToken token;
                lock (_ctsLock)
                {
                    token = _cts.Token;
                }

                await PerformBatchExtractionAsync(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, AppConfig.ChdmanExeName),
                    inputFolder, outputFolder, deleteOriginal, selectedFiles, token);
            }
            catch (OperationCanceledException)
            {
                LogMessage("Extraction canceled.");
            }
            catch (Exception ex)
            {
                LogMessage($"Error: {ex.Message}");
                await ReportBugAsync("Batch extraction error", ex);
            }
            finally
            {
                FinishOperation("Extraction");
            }
        }
        catch (Exception ex)
        {
            _ = ReportBugAsync("StartExtractionButton_Click error", ex);
        }
    }

    private void HandleFolderBrowse(TextBox targetBox, string logName)
    {
        var folder = SelectFolder($"Select {logName} folder");
        if (string.IsNullOrEmpty(folder))
        {
            return;
        }

        var normalized = PathUtils.ValidateAndNormalizePath(folder, logName, ShowError, LogMessage);
        if (normalized != null)
        {
            targetBox.Text = normalized;
            RefreshFileListForActiveTab();
        }

        UpdateStatusBarMessage($"{logName} folder selected");
    }

    private void RefreshFileListForActiveTab()
    {
        if (MainTabControl.SelectedItem is TabItem selectedTab)
        {
            switch (selectedTab.Name)
            {
                case "ConvertTab":
                    _ = LoadFilesForConversionAsync();
                    break;
                case "VerifyTab":
                    _ = LoadFilesForVerificationAsync();
                    break;
                case "ExtractTab":
                    _ = LoadFilesForExtractionAsync();
                    break;
            }
        }
    }

    private Task LoadFilesForConversionAsync()
    {
        var inputFolder = ConversionInputFolderTextBox.Text;
        if (string.IsNullOrEmpty(inputFolder) || !Directory.Exists(inputFolder))
        {
            return Task.CompletedTask;
        }

        var includeSub = SearchSubfoldersConversionCheckBox.IsChecked ?? false;

        return Task.Run(async () =>
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = includeSub,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.System | FileAttributes.Hidden
            };

            var files = Directory.GetFiles(inputFolder, "*.*", options)
                .Where(static file => FileExtensions.AllSupportedInputExtensionsForConversionSet.Contains(Path.GetExtension(file)))
                .Select(f => new FileItem
                {
                    FileName = Path.GetRelativePath(inputFolder, f),
                    FullPath = f,
                    FileSize = new FileInfo(f).Length,
                    IsSelected = true
                }).ToList();

            Application.Current.Dispatcher.Invoke(() => _conversionFiles.Clear());

            // Add items in chunks to avoid freezing the UI thread if there are thousands of files
            const int chunkSize = 100;
            for (var i = 0; i < files.Count; i += chunkSize)
            {
                var chunk = files.Skip(i).Take(chunkSize).ToList();
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    foreach (var item in chunk) _conversionFiles.Add(item);
                    TotalFilesValue.Text = _conversionFiles.Count.ToString(CultureInfo.InvariantCulture);
                }, System.Windows.Threading.DispatcherPriority.Background);
            }
        });
    }

    private Task LoadFilesForVerificationAsync()
    {
        var inputFolder = VerificationInputFolderTextBox.Text;
        if (string.IsNullOrEmpty(inputFolder) || !Directory.Exists(inputFolder))
        {
            return Task.CompletedTask;
        }

        var includeSub = SearchSubfoldersVerificationCheckBox.IsChecked ?? false;

        return Task.Run(async () =>
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = includeSub,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.System | FileAttributes.Hidden
            };

            var files = Directory.GetFiles(inputFolder, "*.chd", options)
                .Where(f =>
                {
                    if (!includeSub)
                    {
                        return true;
                    }

                    var relPath = Path.GetRelativePath(inputFolder, f);
                    var firstPart = relPath.Split(Path.DirectorySeparatorChar)[0];
                    return !firstPart.Equals("Success", StringComparison.OrdinalIgnoreCase) &&
                           !firstPart.Equals("Failed", StringComparison.OrdinalIgnoreCase);
                })
                .Select(f => new FileItem
                {
                    FileName = Path.GetRelativePath(inputFolder, f),
                    FullPath = f,
                    FileSize = new FileInfo(f).Length,
                    IsSelected = true
                }).ToList();

            Application.Current.Dispatcher.Invoke(() => _verificationFiles.Clear());

            // Add items in chunks to avoid freezing the UI thread
            const int chunkSize = 100;
            for (var i = 0; i < files.Count; i += chunkSize)
            {
                var chunk = files.Skip(i).Take(chunkSize).ToList();
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    foreach (var item in chunk) _verificationFiles.Add(item);
                    TotalFilesValue.Text = _verificationFiles.Count.ToString(CultureInfo.InvariantCulture);
                }, System.Windows.Threading.DispatcherPriority.Background);
            }
        });
    }

    private Task LoadFilesForExtractionAsync()
    {
        var inputFolder = ExtractionInputFolderTextBox.Text;
        if (string.IsNullOrEmpty(inputFolder) || !Directory.Exists(inputFolder))
        {
            return Task.CompletedTask;
        }

        var includeSub = SearchSubfoldersExtractionCheckBox.IsChecked ?? false;

        return Task.Run(async () =>
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = includeSub,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.System | FileAttributes.Hidden
            };

            var files = Directory.GetFiles(inputFolder, "*.chd", options)
                .Where(f =>
                {
                    if (!includeSub)
                    {
                        return true;
                    }

                    var relPath = Path.GetRelativePath(inputFolder, f);
                    var firstPart = relPath.Split(Path.DirectorySeparatorChar)[0];
                    return !firstPart.Equals("Success", StringComparison.OrdinalIgnoreCase) &&
                           !firstPart.Equals("Failed", StringComparison.OrdinalIgnoreCase);
                })
                .Select(f => new FileItem
                {
                    FileName = Path.GetRelativePath(inputFolder, f),
                    FullPath = f,
                    FileSize = new FileInfo(f).Length,
                    IsSelected = true
                }).ToList();

            Application.Current.Dispatcher.Invoke(() => _extractionFiles.Clear());

            // Add items in chunks to avoid freezing the UI thread
            const int chunkSize = 100;
            for (var i = 0; i < files.Count; i += chunkSize)
            {
                var chunk = files.Skip(i).Take(chunkSize).ToList();
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    foreach (var item in chunk) _extractionFiles.Add(item);
                    TotalFilesValue.Text = _extractionFiles.Count.ToString(CultureInfo.InvariantCulture);
                }, System.Windows.Threading.DispatcherPriority.Background);
            }
        });
    }

    private void SelectAllConversion_Click(object sender, RoutedEventArgs e)
    {
        foreach (var f in _conversionFiles)
        {
            f.IsSelected = true;
        }
    }

    private void DeselectAllConversion_Click(object sender, RoutedEventArgs e)
    {
        foreach (var f in _conversionFiles)
        {
            f.IsSelected = false;
        }
    }

    private void SelectAllVerification_Click(object sender, RoutedEventArgs e)
    {
        foreach (var f in _verificationFiles)
        {
            f.IsSelected = true;
        }
    }

    private void DeselectAllVerification_Click(object sender, RoutedEventArgs e)
    {
        foreach (var f in _verificationFiles)
        {
            f.IsSelected = false;
        }
    }

    private void SelectAllExtraction_Click(object sender, RoutedEventArgs e)
    {
        foreach (var f in _extractionFiles)
        {
            f.IsSelected = true;
        }
    }

    private void DeselectAllExtraction_Click(object sender, RoutedEventArgs e)
    {
        foreach (var f in _extractionFiles)
        {
            f.IsSelected = false;
        }
    }

    private async void StartConversionButton_ClickAsync(object sender, RoutedEventArgs e)
    {
        try
        {
            await Application.Current.Dispatcher.InvokeAsync((Action)(() => LogViewer.Clear()));
            DisplayConversionInstructionsInLog();

            if (!_isChdmanAvailable)
            {
                ShowError($"{AppConfig.ChdmanExeName} is missing.");
                return;
            }

            var inputFolder = PathUtils.ValidateAndNormalizePath(ConversionInputFolderTextBox.Text, "Source Files Folder", ShowError, LogMessage);
            var outputFolder = PathUtils.ValidateAndNormalizePath(ConversionOutputFolderTextBox.Text, "Output CHD Folder", ShowError, LogMessage);
            if (inputFolder == null || outputFolder == null)
            {
                return;
            }

            if (inputFolder.Equals(outputFolder, StringComparison.OrdinalIgnoreCase))
            {
                ShowError("Input and output folders must be different.");
                return;
            }

            RenewCancellationTokenSource();

            ResetOperationStats();
            SetControlsState(false);
            await Task.Yield();
            _operationTimer.Restart();
            ResetSpeedCounters();

            var deleteFiles = DeleteOriginalsCheckBox.IsChecked ?? false;
            var processSmallerFirst = ProcessSmallerFirstCheckBox.IsChecked ?? false;
            var forceCd = ForceCreateCdCheckBox.IsChecked ?? false;
            var forceDvd = ForceCreateDvdCheckBox.IsChecked ?? false;

            var timeoutEnabled = EnableConversionTimeoutCheckBox.IsChecked ?? false;
            var timeoutMinutes = timeoutEnabled && int.TryParse(ConversionTimeoutTextBox.Text, out var mins) && mins > 0
                ? (int?)mins
                : null;

            var selectedFiles = _conversionFiles.Where(static f => f.IsSelected).Select(static f => f.FullPath).ToArray();
            if (selectedFiles.Length == 0)
            {
                ShowError("No files selected for conversion.");
                return;
            }

            LogMessage("--- Starting batch conversion process... ---");

            try
            {
                CancellationToken token;
                lock (_ctsLock)
                {
                    token = _cts.Token;
                }

                await PerformBatchConversionAsync(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, AppConfig.ChdmanExeName),
                    inputFolder, outputFolder, deleteFiles, processSmallerFirst, forceCd, forceDvd, timeoutMinutes, selectedFiles, token);
            }
            catch (OperationCanceledException)
            {
                LogMessage("Conversion canceled.");
            }
            catch (Exception ex)
            {
                LogMessage($"Error: {ex.Message}");
                await ReportBugAsync("Batch conversion error", ex);
            }
            finally
            {
                FinishOperation("Conversion");
            }
        }
        catch (Exception ex)
        {
            _ = ReportBugAsync("StartConversionButton_Click error", ex);
        }
    }

    private async void StartVerificationButton_ClickAsync(object sender, RoutedEventArgs e)
    {
        try
        {
            await Application.Current.Dispatcher.InvokeAsync((Action)(() => LogViewer.Clear()));
            DisplayVerificationInstructionsInLog();

            if (!_isChdmanAvailable)
            {
                ShowError($"{AppConfig.ChdmanExeName} is missing.");
                return;
            }

            var inputFolder = PathUtils.ValidateAndNormalizePath(VerificationInputFolderTextBox.Text, "CHD Files Folder", ShowError, LogMessage);
            if (inputFolder == null)
            {
                return;
            }

            RenewCancellationTokenSource();

            ResetOperationStats();
            SetControlsState(false);
            await Task.Yield();
            _operationTimer.Restart();
            ResetSpeedCounters();

            var includeSubfolders = SearchSubfoldersVerificationCheckBox.IsChecked ?? false;
            var moveSuccess = MoveSuccessFilesCheckBox.IsChecked ?? false;
            var moveFailed = MoveFailedFilesCheckBox.IsChecked ?? false;
            var successFolder = moveSuccess ? Path.Combine(inputFolder, "Success") : string.Empty;
            var failedFolder = moveFailed ? Path.Combine(inputFolder, "Failed") : string.Empty;

            var selectedFiles = _verificationFiles.Where(static f => f.IsSelected).Select(static f => f.FullPath).ToArray();
            if (selectedFiles.Length == 0)
            {
                ShowError("No files selected for verification.");
                return;
            }

            LogMessage("--- Starting batch verification process... ---");

            try
            {
                CancellationToken token;
                lock (_ctsLock)
                {
                    token = _cts.Token;
                }

                await PerformBatchVerificationAsync(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, AppConfig.ChdmanExeName),
                    inputFolder, includeSubfolders, moveSuccess, successFolder, moveFailed, failedFolder, selectedFiles, token);
            }
            catch (OperationCanceledException)
            {
                LogMessage("Verification canceled.");
            }
            catch (Exception ex)
            {
                LogMessage($"Error: {ex.Message}");
                await ReportBugAsync("Batch verification error", ex);
            }
            finally
            {
                FinishOperation("Verification");
            }
        }
        catch (Exception ex)
        {
            _ = ReportBugAsync("StartVerificationButton_Click error", ex);
        }
    }

    private void FinishOperation(string opName)
    {
        _operationTimer.Stop();
        UpdateProcessingTimeDisplay();
        UpdateWriteSpeedDisplay(0);
        UpdateReadSpeedDisplay(0);
        SetControlsState(true);
        LogOperationSummary(opName);

        // Clear progress display
        ClearProgressDisplay();

        if (_pendingClose)
        {
            Close();
        }
    }

    private void RenewCancellationTokenSource()
    {
        lock (_ctsLock)
        {
            _cts.Cancel();
            _cts.Dispose();
            _cts = new CancellationTokenSource();
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        lock (_ctsLock)
        {
            _cts.Cancel();
        }

        LogMessage("Cancellation requested...");
        UpdateStatusBarMessage("Cancelling...");
    }

    private void SetControlsState(bool enabled)
    {
        // Thread-safely update operation state (0 = idle, 1 = running)
        Interlocked.Exchange(ref _operationRunningState, enabled ? 0 : 1);

        ConversionInputFolderTextBox.IsEnabled = enabled;
        BrowseConversionInputButton.IsEnabled = enabled;
        ConversionOutputFolderTextBox.IsEnabled = enabled;
        BrowseConversionOutputButton.IsEnabled = enabled;
        SearchSubfoldersConversionCheckBox.IsEnabled = enabled;
        DeleteOriginalsCheckBox.IsEnabled = enabled;
        ProcessSmallerFirstCheckBox.IsEnabled = enabled;
        StartConversionButton.IsEnabled = enabled;
        ForceCreateCdCheckBox.IsEnabled = enabled;
        ForceCreateDvdCheckBox.IsEnabled = enabled;
        VerificationInputFolderTextBox.IsEnabled = enabled;
        BrowseVerificationInputButton.IsEnabled = enabled;
        SearchSubfoldersVerificationCheckBox.IsEnabled = enabled;
        StartVerificationButton.IsEnabled = enabled;
        MoveSuccessFilesCheckBox.IsEnabled = enabled;
        MoveFailedFilesCheckBox.IsEnabled = enabled;
        ExtractionInputFolderTextBox.IsEnabled = enabled;
        BrowseExtractionInputButton.IsEnabled = enabled;
        ExtractionOutputFolderTextBox.IsEnabled = enabled;
        BrowseExtractionOutputButton.IsEnabled = enabled;
        SearchSubfoldersExtractionCheckBox.IsEnabled = enabled;
        DeleteOriginalChdCheckBox.IsEnabled = enabled;
        ExtractAutoRadioButton.IsEnabled = enabled;
        ExtractCdRadioButton.IsEnabled = enabled;
        ExtractDvdRadioButton.IsEnabled = enabled;
        ExtractGdiRadioButton.IsEnabled = enabled;
        ExtractHdRadioButton.IsEnabled = enabled;
        StartExtractionButton.IsEnabled = enabled;
        MainTabControl.IsEnabled = enabled;

        // Toggle progress area visibility
        ProgressAreaGrid.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        ProgressText.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        ProgressBar.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        ProgressBar.IsIndeterminate = !enabled; // Start moving immediately
        CancelButton.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;

        if (!enabled)
        {
            var tab = MainTabControl.SelectedItem as TabItem;
            var message = tab?.Name switch
            {
                "ConvertTab" => "Converting files...",
                "VerifyTab" => "Verifying files...",
                "ExtractTab" => "Extracting files...",
                _ => "Processing..."
            };
            UpdateStatusBarMessage(message);
        }
        else
        {
            ClearProgressDisplay();
            UpdateWriteSpeedDisplay(0);
            UpdateReadSpeedDisplay(0);
        }
    }

    private static string? SelectFolder(string description)
    {
        var dialog = new OpenFolderDialog { Title = description };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    private async Task PerformBatchConversionAsync(string chdmanPath, string inputFolder, string outputFolder, bool deleteFiles, bool processSmallerFirst, bool forceCd, bool forceDvd, int? timeoutMinutes, string[] selectedFiles, CancellationToken token)
    {
        if (!await ValidateExecutableAccessAsync(chdmanPath, "chdman.exe")) return;

        if (!await ValidateChdmanCompatibilityAsync(chdmanPath, token)) return;

        var filesToConvert = selectedFiles;

        if (processSmallerFirst)
        {
            filesToConvert = filesToConvert.OrderBy(static f =>
            {
                try
                {
                    return new FileInfo(f).Length;
                }
                catch
                {
                    return 0;
                }
            }).ToArray();
        }

        _totalFilesProcessed = filesToConvert.Length;
        UpdateStatsDisplay();
        LogMessage($"Found {_totalFilesProcessed} files to process.");
        if (_totalFilesProcessed == 0)
        {
            return;
        }

        CheckDiskSpace(outputFolder, filesToConvert, true);

        await Application.Current.Dispatcher.InvokeAsync(() => ProgressBar.Maximum = _totalFilesProcessed);
        var processedCount = 0;
        var cores = Environment.ProcessorCount;
        ResetSpeedCounters();

        foreach (var file in filesToConvert)
        {
            token.ThrowIfCancellationRequested();

            // Update text to show we are starting this file, but bar stays at 'processedCount'
            UpdateProgressDisplay(processedCount, _totalFilesProcessed, Path.GetFileName(file), "Converting");

            var success = await ProcessSingleFileForConversionAsync(chdmanPath, file, inputFolder, outputFolder, deleteFiles, cores, forceCd, forceDvd, timeoutMinutes, token);
            if (success)
            {
                _processedOkCount++;
            }
            else
            {
                _failedCount++;
            }

            processedCount++;
            UpdateProgressDisplay(processedCount, _totalFilesProcessed, Path.GetFileName(file), "Finishing");
            UpdateStatsDisplay();
            UpdateProcessingTimeDisplay();
            UpdateWriteSpeedFromPerformanceCounter();
        }
    }

    private async Task PerformBatchExtractionAsync(string chdmanPath, string inputFolder, string outputFolder, bool deleteOriginal, string[] selectedFiles, CancellationToken token)
    {
        if (!await ValidateExecutableAccessAsync(chdmanPath, "chdman.exe")) return;
        if (!await ValidateChdmanCompatibilityAsync(chdmanPath, token)) return;

        _totalFilesProcessed = selectedFiles.Length;
        UpdateStatsDisplay();
        LogMessage($"Found {_totalFilesProcessed} CHD files to extract.");
        if (_totalFilesProcessed == 0)
        {
            return;
        }

        CheckDiskSpace(outputFolder, selectedFiles, false);

        await Application.Current.Dispatcher.InvokeAsync(() => ProgressBar.Maximum = _totalFilesProcessed);
        var processedCount = 0;
        ResetSpeedCounters();

        foreach (var file in selectedFiles)
        {
            token.ThrowIfCancellationRequested();

            UpdateProgressDisplay(processedCount, _totalFilesProcessed, Path.GetFileName(file), "Extracting");

            var success = await ExtractChdAsync(chdmanPath, file, inputFolder, outputFolder, deleteOriginal, token);
            if (success)
            {
                _processedOkCount++;
            }
            else
            {
                _failedCount++;
            }

            processedCount++;
            UpdateProgressDisplay(processedCount, _totalFilesProcessed, Path.GetFileName(file), "Finishing");
            UpdateStatsDisplay();
            UpdateProcessingTimeDisplay();
            UpdateReadSpeedFromPerformanceCounter();
        }
    }

    private async Task<bool> ProcessSingleFileForConversionAsync(string chdmanPath, string inputFile, string inputFolder, string outputFolder, bool deleteOriginal, int cores, bool forceCd, bool forceDvd, int? timeoutMinutes, CancellationToken token)
    {
        inputFile = Path.GetFullPath(inputFile);
        var originalName = Path.GetFileName(inputFile);
        LogMessage($"Processing: {originalName}");

        if (!File.Exists(inputFile))
        {
            LogMessage($"WARNING: File not found, skipping: {inputFile}");
            return false;
        }

        var ext = Path.GetExtension(inputFile);
        var tempDirs = new List<string>();
        var outputChd = string.Empty;

        try
        {
            token.ThrowIfCancellationRequested();
            var chdBase = Path.GetFileNameWithoutExtension(originalName);

            // Maintain directory structure if searching subfolders
            var relativePath = PathUtils.GetSafeRelativePath(inputFolder, Path.GetDirectoryName(inputFile) ?? inputFolder);
            var targetDir = relativePath == "." ? outputFolder : Path.Combine(outputFolder, relativePath);

            outputChd = Path.Combine(targetDir, PathUtils.SanitizeFileName(chdBase) + FileExtensions.Chd);

            string fileToProcess;
            if (ext.Equals(FileExtensions.Cso, StringComparison.OrdinalIgnoreCase))
            {
                long csoSize = 0;
                try
                {
                    csoSize = new FileInfo(inputFile).Length;
                }
                catch
                {
                    /* ignored */
                }

                var tempDir = PathUtils.GetBestTempDirectory(inputFile, outputFolder, TempDirPrefix, csoSize);
                tempDirs.Add(tempDir);
                await Task.Run(() => Directory.CreateDirectory(tempDir), token);
                var tempIso = PathUtils.GetSafeTempFileName(originalName, "iso", tempDir);

                var result = await _archiveService.ExtractCsoAsync(inputFile, tempIso, tempDir, LogMessage, token);
                if (!result.Success)
                {
                    return false;
                }

                fileToProcess = result.FilePath;
            }
            else if (FileExtensions.ArchiveExtensionsSet.Contains(ext))
            {
                long archiveSize = 0;
                try
                {
                    archiveSize = new FileInfo(inputFile).Length;
                }
                catch
                {
                    /* ignored */
                }

                var tempDir = PathUtils.GetBestTempDirectory(inputFile, outputFolder, TempDirPrefix, archiveSize);
                tempDirs.Add(tempDir);
                var result = await _archiveService.ExtractArchiveAsync(inputFile, tempDir, LogMessage, token);
                if (!result.Success)
                {
                    if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
                    {
                        LogMessage($"ERROR: {result.ErrorMessage}");
                    }

                    return false;
                }

                // Convert all supported files found in the archive
                var allSucceeded = true;
                foreach (var extractedFile in result.FilePaths)
                {
                    token.ThrowIfCancellationRequested();
                    var extractedFileOutputChd = ComputeOutputChdPathForExtractedFile(extractedFile, inputFile, inputFolder, outputFolder);
                    var extractedOutputDir = Path.GetDirectoryName(extractedFileOutputChd) ?? outputFolder;
                    if (!Directory.Exists(extractedOutputDir)) Directory.CreateDirectory(extractedOutputDir);

                    LogMessage($"Converting extracted file: {Path.GetFileName(extractedFile)}");
                    var converted = await ConvertToChdAsync(chdmanPath, extractedFile, extractedFileOutputChd, cores, forceCd, forceDvd, timeoutMinutes, token);
                    if (!converted)
                    {
                        LogMessage($"Failed to convert: {Path.GetFileName(extractedFile)}");
                        await TryDeleteFileAsync(extractedFileOutputChd, "failed CHD", CancellationToken.None);
                        allSucceeded = false;
                    }
                }

                if (allSucceeded && deleteOriginal)
                {
                    await TryDeleteFileAsync(inputFile, "original archive", token);
                }

                return allSucceeded;
            }
            else if (ext.Equals(FileExtensions.Pbp, StringComparison.OrdinalIgnoreCase))
            {
                if (!_isPsxPackagerAvailable)
                {
                    LogMessage($"ERROR: Cannot process {originalName}. psxpackager.exe not found.");
                    return false;
                }

                long pbpSize = 0;
                try
                {
                    pbpSize = new FileInfo(inputFile).Length;
                }
                catch
                {
                    /* ignored */
                }

                var tempDir = PathUtils.GetBestTempDirectory(inputFile, outputFolder, TempDirPrefix, pbpSize);
                tempDirs.Add(tempDir);
                await Task.Run(() => Directory.CreateDirectory(tempDir), token);

                var result = await ExtractPbpToCueBinAsync(_psxPackagerPath, inputFile, tempDir, token);
                if (!result.Success || result.CueFilePaths.Count == 0)
                {
                    LogMessage($"ERROR: Failed to extract PBP file: {originalName}");
                    return false;
                }

                // Convert all extracted CUE files (multi-disc PBP produces multiple CUE files)
                var allSucceeded = true;
                foreach (var cueFile in result.CueFilePaths)
                {
                    token.ThrowIfCancellationRequested();
                    var cueFileOutputChd = ComputeOutputChdPathForExtractedFile(cueFile, inputFile, inputFolder, outputFolder);
                    var cueOutputDir = Path.GetDirectoryName(cueFileOutputChd) ?? outputFolder;
                    if (!Directory.Exists(cueOutputDir)) Directory.CreateDirectory(cueOutputDir);

                    if (result.CueFilePaths.Count > 1)
                    {
                        LogMessage($"Converting disc: {Path.GetFileName(cueFile)}");
                    }

                    var converted = await ConvertToChdAsync(chdmanPath, cueFile, cueFileOutputChd, cores, forceCd, forceDvd, timeoutMinutes, token);
                    if (!converted)
                    {
                        LogMessage($"Failed to convert: {Path.GetFileName(cueFile)}");
                        await TryDeleteFileAsync(cueFileOutputChd, "failed CHD", CancellationToken.None);
                        allSucceeded = false;
                    }
                }

                if (allSucceeded && deleteOriginal)
                {
                    await TryDeleteFileAsync(inputFile, "original PBP", token);
                }

                return allSucceeded;
            }
            else
            {
                // Try processing directly from source first to avoid unnecessary I/O
                fileToProcess = inputFile;
            }

            if (ext is FileExtensions.Cue or FileExtensions.Gdi or FileExtensions.Toc or FileExtensions.Ccd)
            {
                try
                {
                    var referencedFiles = ext switch
                    {
                        FileExtensions.Cue => await GameFileParser.GetReferencedFilesFromCueAsync(inputFile, LogMessage, token),
                        FileExtensions.Gdi => await GameFileParser.GetReferencedFilesFromGdiAsync(inputFile, LogMessage, token),
                        FileExtensions.Ccd => [Path.ChangeExtension(inputFile, FileExtensions.Img), Path.ChangeExtension(inputFile, FileExtensions.Sub)],
                        _ => await GameFileParser.GetReferencedFilesFromTocAsync(inputFile, LogMessage, token)
                    };

                    var missingFiles = referencedFiles.Where(static f => !File.Exists(f)).ToList();
                    if (missingFiles.Count > 0)
                    {
                        var missingNames = string.Join(", ", missingFiles.Select(Path.GetFileName));
                        LogMessage($"SKIPPING: {originalName} — referenced files are missing: {missingNames}");
                        return false;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogMessage($"SKIPPING: {originalName} — could not validate referenced files: {ex.Message}");
                    return false;
                }
            }

            UpdateWriteSpeedDisplay(0);
            var outputDir = Path.GetDirectoryName(outputChd) ?? outputFolder;
            if (!Directory.Exists(outputDir)) Directory.CreateDirectory(outputDir);

            var success = false;
            try
            {
                success = await ConvertToChdAsync(chdmanPath, fileToProcess, outputChd, cores, forceCd, forceDvd, timeoutMinutes, token);
            }
            catch (Exception ex)
            {
                if (IsCancellationException(ex))
                    throw;

                if (IsDiskSpaceException(ex))
                {
                    LogMessage($"ERROR: Not enough disk space to convert {originalName}. Free up disk space and try again.");
                }
                else
                {
                    LogMessage($"Direct conversion attempt error for {originalName}: {ex.Message}");
                }

                if (!IsDiskSpaceException(ex))
                {
                    _ = ReportBugAsync($"Direct conversion attempt error for {originalName}", ex);
                }
            }

            // Fallback: If direct conversion failed and we haven't already extracted to temp (i.e. it was a direct file attempt),
            // try copying to temp and converting there. This handles network path issues or file locking quirks.
            if (!success && fileToProcess == inputFile && !token.IsCancellationRequested)
            {
                LogMessage($"Direct conversion failed for {originalName}. Retrying via temporary directory copy...");
                await TryDeleteFileAsync(outputChd, "failed partial CHD", CancellationToken.None);

                try
                {
                    // Determine files to copy and total size before selecting temp directory
                    List<string> filesToCopy;
                    if (ext is FileExtensions.Cue or FileExtensions.Gdi or FileExtensions.Toc or FileExtensions.Ccd)
                    {
                        filesToCopy = [inputFile];
                        switch (ext)
                        {
                            case FileExtensions.Cue:
                                filesToCopy.AddRange(await GameFileParser.GetReferencedFilesFromCueAsync(inputFile, LogMessage, token));
                                break;
                            case FileExtensions.Gdi:
                                filesToCopy.AddRange(await GameFileParser.GetReferencedFilesFromGdiAsync(inputFile, LogMessage, token));
                                break;
                            case FileExtensions.Ccd:
                                var imgFile = Path.ChangeExtension(inputFile, FileExtensions.Img);
                                var subFile = Path.ChangeExtension(inputFile, FileExtensions.Sub);
                                if (File.Exists(imgFile)) filesToCopy.Add(imgFile);
                                if (File.Exists(subFile)) filesToCopy.Add(subFile);
                                break;
                            // .toc
                            default:
                                filesToCopy.AddRange(await GameFileParser.GetReferencedFilesFromTocAsync(inputFile, LogMessage, token));
                                break;
                        }

                        var missingFiles = filesToCopy.Distinct().Where(static f => !File.Exists(f)).ToList();
                        if (missingFiles.Count > 0)
                        {
                            var missingNames = string.Join(", ", missingFiles.Select(Path.GetFileName));
                            LogMessage($"WARNING: Skipping temp retry for {originalName} because referenced files are missing: {missingNames}");
                            return false;
                        }
                    }
                    else
                    {
                        filesToCopy = [inputFile];
                    }

                    // Calculate total bytes needed for temp copy
                    long totalBytesNeeded = 0;
                    foreach (var file in filesToCopy.Distinct())
                    {
                        try
                        {
                            totalBytesNeeded += new FileInfo(file).Length;
                        }
                        catch
                        {
                            /* skip inaccessible files */
                        }
                    }

                    // Select temp directory preferring a drive with enough space
                    var tempDir = PathUtils.GetBestTempDirectory(inputFile, outputFolder, TempDirPrefix, totalBytesNeeded);
                    tempDirs.Add(tempDir);
                    await Task.Run(() => Directory.CreateDirectory(tempDir), token);

                    // Verify the selected temp drive has enough space
                    try
                    {
                        var tempDriveRoot = Path.GetPathRoot(tempDir);
                        if (!string.IsNullOrEmpty(tempDriveRoot))
                        {
                            var tempDrive = new DriveInfo(tempDriveRoot);
                            if (tempDrive.IsReady && tempDrive.AvailableFreeSpace < totalBytesNeeded)
                            {
                                var availableGb = tempDrive.AvailableFreeSpace / (1024.0 * 1024.0 * 1024.0);
                                var neededGb = totalBytesNeeded / (1024.0 * 1024.0 * 1024.0);
                                LogMessage($"ERROR: Not enough disk space for temp copy of {originalName}. Need {neededGb:F1} GB but only {availableGb:F1} GB available on {tempDriveRoot.TrimEnd('\\')}.");
                                return false;
                            }
                        }
                    }
                    catch
                    {
                        // If drive info check fails, proceed with the copy attempt anyway
                    }

                    string tempInputFile;

                    if (ext is FileExtensions.Cue or FileExtensions.Gdi or FileExtensions.Toc or FileExtensions.Ccd)
                    {
                        LogMessage("Copying game with dependencies to temporary directory...");
                        foreach (var file in filesToCopy.Distinct())
                        {
                            var destPath = Path.Combine(tempDir, Path.GetFileName(file));
                            await CopyFileWithRetryAsync(file, destPath, token);
                        }

                        tempInputFile = Path.Combine(tempDir, Path.GetFileName(inputFile));
                    }
                    else
                    {
                        // Original logic for single, non-dependent files
                        tempInputFile = Path.Combine(tempDir, originalName);
                        await CopyFileWithRetryAsync(inputFile, tempInputFile, token);
                    }

                    fileToProcess = tempInputFile;
                    success = await ConvertToChdAsync(chdmanPath, fileToProcess, outputChd, cores, forceCd, forceDvd, timeoutMinutes, token);
                }
                catch (Exception ex)
                {
                    if (IsCancellationException(ex))
                        throw;

                    if (IsDiskSpaceException(ex))
                    {
                        LogMessage($"ERROR: Not enough disk space to convert {originalName} (via temp). Free up disk space and try again.");
                    }
                    else if (IsCorruptionException(ex) || IsCrcErrorException(ex))
                    {
                        LogMessage($"ERROR: Source file appears to be corrupt: {originalName}");
                    }
                    else
                    {
                        LogMessage($"Retry via temp failed for {originalName} ({inputFile}): {ex.Message}");
                    }

                    if (!IsDiskSpaceException(ex) && !IsCorruptionException(ex) && !IsCrcErrorException(ex))
                    {
                        _ = ReportBugAsync($"Retry via temp failed for {originalName}", ex);
                    }
                }
            }

            if (success)
            {
                LogMessage($"Converted: {originalName}");
                if (deleteOriginal)
                {
                    LogMessage($"Deleting source: {originalName} (Option 'Delete originals' is enabled)");

                    var isImgWithCcd = ext == FileExtensions.Img && File.Exists(Path.ChangeExtension(inputFile, FileExtensions.Ccd));

                    if (ext is FileExtensions.Cue or FileExtensions.Gdi or FileExtensions.Toc or FileExtensions.Ccd || isImgWithCcd)
                    {
                        // Parse and delete dependencies + input file
                        await DeleteOriginalGameFilesAsync(inputFile, token);
                    }
                    else
                    {
                        // Just delete input file
                        await TryDeleteFileAsync(inputFile, "original file", token);
                    }

                    // Clean up empty parent folder if it's a subfolder
                    var subfolder = Path.GetDirectoryName(inputFile);
                    if (!string.IsNullOrEmpty(subfolder))
                    {
                        await TryDeleteEmptySubfolderAsync(subfolder, inputFolder, token);
                    }
                }

                return true;
            }
            else
            {
                if (deleteOriginal)
                {
                    LogMessage($"KEEPING source: {originalName} (Conversion failed, skipping deletion for safety)");
                }

                await TryDeleteFileAsync(outputChd, "failed CHD", CancellationToken.None);
                return false;
            }
        }
        catch (OperationCanceledException)
        {
            if (!string.IsNullOrEmpty(outputChd)) await TryDeleteFileAsync(outputChd, "incomplete CHD (cancelled)", CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            if (IsDiskSpaceException(ex))
            {
                LogMessage($"ERROR: Not enough disk space to process {originalName}. Free up disk space and try again.");
            }
            else if (IsCorruptionException(ex))
            {
                LogMessage($"ERROR: Archive appears to be corrupt or unsupported: {originalName}");
            }
            else
            {
                LogMessage($"Error processing {originalName}: {ex.Message}");
            }

            if (!IsDiskSpaceException(ex) && !IsCorruptionException(ex))
            {
                _ = ReportBugAsync($"Error processing {originalName}", ex);
            }

            if (!string.IsNullOrEmpty(outputChd)) await TryDeleteFileAsync(outputChd, "failed CHD", CancellationToken.None);
            return false;
        }
        finally
        {
            foreach (var tempDir in tempDirs)
            {
                if (!string.IsNullOrEmpty(tempDir) && Directory.Exists(tempDir))
                    await TryDeleteDirectoryAsync(tempDir, "temp dir", CancellationToken.None);
            }
        }
    }

    private static string ComputeOutputChdPathForExtractedFile(string extractedFilePath, string originalInputFile, string inputFolder, string outputFolder)
    {
        // Use the original input file (e.g. the archive) to determine the relative path
        var relativePath = PathUtils.GetSafeRelativePath(inputFolder, Path.GetDirectoryName(originalInputFile) ?? inputFolder);
        var targetDir = relativePath == "." ? outputFolder : Path.Combine(outputFolder, relativePath);
        var chdBase = Path.GetFileNameWithoutExtension(extractedFilePath);
        return Path.Combine(targetDir, PathUtils.SanitizeFileName(chdBase) + FileExtensions.Chd);
    }

    private async Task PerformBatchVerificationAsync(string chdmanPath, string inputFolder, bool includeSub, bool moveSuccess, string successFolder, bool moveFailed, string failedFolder, string[] selectedFiles, CancellationToken token)
    {
        if (!await ValidateExecutableAccessAsync(chdmanPath, "chdman.exe")) return;
        if (!await ValidateChdmanCompatibilityAsync(chdmanPath, token)) return;

        _totalFilesProcessed = selectedFiles.Length;
        UpdateStatsDisplay();
        LogMessage($"Found {_totalFilesProcessed} CHD files to verify.");
        if (_totalFilesProcessed == 0)
        {
            return;
        }

        // Create success/failed folders if needed
        if (moveSuccess && !string.IsNullOrEmpty(successFolder) && !Directory.Exists(successFolder))
        {
            Directory.CreateDirectory(successFolder);
        }

        if (moveFailed && !string.IsNullOrEmpty(failedFolder) && !Directory.Exists(failedFolder))
        {
            Directory.CreateDirectory(failedFolder);
        }

        await Application.Current.Dispatcher.InvokeAsync(() => ProgressBar.Maximum = _totalFilesProcessed);
        var processed = 0;
        ResetSpeedCounters();

        foreach (var file in selectedFiles)
        {
            token.ThrowIfCancellationRequested();

            // Show current file in text, but bar shows 'processed' (completed) count
            UpdateProgressDisplay(processed, _totalFilesProcessed, Path.GetFileName(file), "Verifying");

            var success = await VerifyChdAsync(chdmanPath, file, token);

            if (success)
            {
                LogMessage($"✓ Verified: {Path.GetFileName(file)}");
                _processedOkCount++;

                // Move to success folder if option is enabled
                if (moveSuccess && !string.IsNullOrEmpty(successFolder))
                {
                    await MoveVerifiedFileAsync(file, successFolder, inputFolder, includeSub, token);
                }
            }
            else
            {
                LogMessage($"✗ Failed: {Path.GetFileName(file)}");
                _failedCount++;

                // Move to failed folder if option is enabled
                if (moveFailed && !string.IsNullOrEmpty(failedFolder))
                {
                    await MoveVerifiedFileAsync(file, failedFolder, inputFolder, includeSub, token);
                }
            }

            processed++;
            UpdateProgressDisplay(processed, _totalFilesProcessed, Path.GetFileName(file), "Finishing");
            UpdateStatsDisplay();
            UpdateProcessingTimeDisplay();
            UpdateReadSpeedFromPerformanceCounter();
        }
    }

    private static async Task MoveVerifiedFileAsync(string sourceFile, string targetFolder, string inputFolder, bool includeSub, CancellationToken token)
    {
        try
        {
            string destFile;
            if (includeSub)
            {
                // Maintain directory structure
                var relativePath = PathUtils.GetSafeRelativePath(inputFolder, Path.GetDirectoryName(sourceFile) ?? inputFolder);
                var targetSubDir = relativePath == "." ? targetFolder : Path.Combine(targetFolder, relativePath);
                if (!Directory.Exists(targetSubDir))
                {
                    Directory.CreateDirectory(targetSubDir);
                }

                destFile = Path.Combine(targetSubDir, Path.GetFileName(sourceFile));
            }
            else
            {
                destFile = Path.Combine(targetFolder, Path.GetFileName(sourceFile));
            }

            // Delete destination if it already exists
            if (File.Exists(destFile))
            {
                File.Delete(destFile);
            }

            await Task.Run(() => File.Move(sourceFile, destFile), token);
        }
        catch (Exception ex)
        {
            // Log error but don't fail the verification
            _ = ReportBugAsync($"Failed to move file {sourceFile}", ex);
        }
    }

    private async Task<bool> ExtractChdAsync(string chdmanPath, string chdFile, string inputFolder, string outputFolder, bool deleteOriginal, CancellationToken token)
    {
        var fileName = Path.GetFileNameWithoutExtension(chdFile);

        // Maintain directory structure if searching subfolders
        var relativePath = PathUtils.GetSafeRelativePath(inputFolder, Path.GetDirectoryName(chdFile) ?? inputFolder);
        var targetDir = relativePath == "." ? outputFolder : Path.Combine(outputFolder, relativePath);
        if (!Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);

        // Get extraction command based on user-selected output format
        var extractCommand = await GetSelectedExtractCommandAsync(chdmanPath, chdFile, token);

        // Determine output extension based on selection or detected command
        var outputExt = FileExtensions.Cue; // Default for extractcd
        if (ExtractGdiRadioButton.IsChecked == true)
        {
            outputExt = FileExtensions.Gdi;
        }
        else if (ExtractDvdRadioButton.IsChecked == true)
        {
            outputExt = FileExtensions.Iso;
        }
        else if (ExtractHdRadioButton.IsChecked == true)
        {
            outputExt = FileExtensions.Img;
        }
        else if (ExtractAutoRadioButton.IsChecked == true)
        {
            outputExt = extractCommand switch
            {
                "extractdvd" => FileExtensions.Iso,
                "extracthd" => FileExtensions.Img,
                _ => FileExtensions.Cue
            };

            if (extractCommand == "extractcd" && await IsGdiChdAsync(chdmanPath, chdFile, token))
            {
                outputExt = FileExtensions.Gdi;
            }
        }

        var outputFile = Path.Combine(targetDir, fileName + outputExt);

        // Delete existing output file if it exists (will be overwritten)
        if (File.Exists(outputFile))
        {
            LogMessage($"Overwriting: {fileName}{outputExt} already exists in output folder.");
            await TryDeleteFileAsync(outputFile, "existing output file", CancellationToken.None);
        }

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = chdmanPath,
            Arguments = $"{extractCommand} -i \"{chdFile}\" -o \"{outputFile}\" -f",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(chdmanPath),
            ErrorDialog = false
        };

        process.OutputDataReceived += (_, a) =>
        {
            if (string.IsNullOrEmpty(a.Data)) return;

            // Filter progress messages but log important ones
            if (a.Data.Contains("% complete"))
            {
                UpdateReadSpeedFromPerformanceCounter();
            }
            else if (!a.Data.Contains("Extracting") && !a.Data.Contains("hunk"))
            {
                LogMessage($"[CHDMAN] {a.Data}");
            }
        };

        var errorBuffer = new StringBuilder();
        process.ErrorDataReceived += (_, a) =>
        {
            if (string.IsNullOrEmpty(a.Data)) return;

            errorBuffer.AppendLine(a.Data);

            // Filter progress messages but log important ones
            if (a.Data.Contains("% complete"))
            {
                UpdateReadSpeedFromPerformanceCounter();
            }
            else if (!a.Data.Contains("Extracting") && !a.Data.Contains("hunk"))
            {
                LogMessage($"[CHDMAN] {a.Data}");
            }
        };

        using var ctsSpeed = CancellationTokenSource.CreateLinkedTokenSource(token);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var speedToken = ctsSpeed.Token;
        var readSpeedTask = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(100, speedToken);
                while (!speedToken.IsCancellationRequested)
                {
                    UpdateReadSpeedFromPerformanceCounter();
                    await Task.Delay(AppConfig.WriteSpeedUpdateIntervalMs, speedToken);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, speedToken);

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeoutCts.CancelAfter(TimeSpan.FromHours(AppConfig.MaxConversionTimeoutHours));
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Wait a bit more for file handles to be fully released
            await Task.Delay(500, CancellationToken.None);
            // Clean up partially extracted file
            await TryDeleteFileAsync(outputFile, "partially extracted file", CancellationToken.None);
            throw;
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(true);
                await Task.Run(() => process.WaitForExit(5000), CancellationToken.None);
            }

            ctsSpeed.Cancel();
            await Task.WhenAny(readSpeedTask, Task.Delay(500, CancellationToken.None));
            // Ensure output streams are properly drained before disposal
            process.CancelOutputRead();
            process.CancelErrorRead();
        }

        var success = process.ExitCode == 0 && !token.IsCancellationRequested;

        switch (success)
        {
            case false when !token.IsCancellationRequested && IsDiskSpaceError(errorBuffer.ToString()):
                LogMessage($"ERROR: Extraction of '{Path.GetFileName(chdFile)}' failed due to insufficient disk space.");
                LogMessage("       Free up disk space on the output drive and try again.");
                break;
            case true when deleteOriginal:
                await TryDeleteFileAsync(chdFile, "original CHD file", token);
                break;
        }

        return success;
    }

    private async Task<string> GetSelectedExtractCommandAsync(string chdmanPath, string chdFile, CancellationToken token)
    {
        if (ExtractAutoRadioButton.IsChecked == true)
            return await DetectChdExtractCommandAsync(chdmanPath, chdFile, token);
        if (ExtractDvdRadioButton.IsChecked == true)
            return "extractdvd";
        if (ExtractHdRadioButton.IsChecked == true)
            return "extracthd";

        // Both CD and GDI use the 'extractcd' command in chdman
        return "extractcd";
    }

    private static async Task<bool> IsGdiChdAsync(string chdmanPath, string chdFile, CancellationToken token)
    {
        using var process = new Process();
        try
        {
            process.StartInfo = new ProcessStartInfo
            {
                FileName = chdmanPath,
                Arguments = $"info -i \"{chdFile}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                ErrorDialog = false
            };

            var output = new StringBuilder();
            process.OutputDataReceived += (_, a) =>
            {
                if (!string.IsNullOrEmpty(a.Data)) output.AppendLine(a.Data);
            };
            process.Start();
            process.BeginOutputReadLine();
            await process.WaitForExitAsync(token);

            return output.ToString().Contains("gd-rom", StringComparison.OrdinalIgnoreCase);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(true);
                    await Task.Run(() => process.WaitForExit(5000), CancellationToken.None);
                }
                catch
                {
                    // Best effort - ignore errors during cleanup
                }
            }

            throw;
        }
        catch
        {
            // Ensure process is terminated on any other exception before returning false
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(true);
                    await Task.Run(() => process.WaitForExit(5000), CancellationToken.None);
                }
                catch
                {
                    // Best effort - ignore errors during cleanup
                }
            }

            return false;
        }
        finally
        {
            // Ensure output streams are properly drained before disposal
            try
            {
                process.CancelOutputRead();
            }
            catch
            {
                // Best effort - ignore errors during cleanup
            }
        }
    }

    private static async Task<string> DetectChdExtractCommandAsync(string chdmanPath, string chdFile, CancellationToken token)
    {
        using var process = new Process();
        try
        {
            process.StartInfo = new ProcessStartInfo
            {
                FileName = chdmanPath,
                Arguments = $"info -i \"{chdFile}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                ErrorDialog = false
            };

            var output = new StringBuilder();
            process.OutputDataReceived += (_, a) =>
            {
                if (!string.IsNullOrEmpty(a.Data))
                    output.AppendLine(a.Data);
            };
            process.ErrorDataReceived += (_, a) =>
            {
                if (!string.IsNullOrEmpty(a.Data))
                    output.AppendLine(a.Data);
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync(token);

            var infoText = output.ToString();

            // Determine extraction command based on CHD metadata
            if (infoText.Contains("dvd", StringComparison.OrdinalIgnoreCase))
                return "extractdvd";
            if (infoText.Contains("gd-rom", StringComparison.OrdinalIgnoreCase))
                return "extractcd";
            if (infoText.Contains("hard disk", StringComparison.OrdinalIgnoreCase) ||
                infoText.Contains("hdd", StringComparison.OrdinalIgnoreCase))
                return "extracthd";

            // Default to CD extraction (most common)
            return "extractcd";
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(true);
            }

            await Task.Run(() => process.WaitForExit(5000), CancellationToken.None);
            throw;
        }
        catch
        {
            // Ensure process is terminated on any other exception before returning default
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(true);
                    await Task.Run(() => process.WaitForExit(5000), CancellationToken.None);
                }
                catch
                {
                    // Best effort - ignore errors during cleanup
                }
            }

            // Default to extractcd if detection fails
            return "extractcd";
        }
        finally
        {
            // Ensure output streams are properly drained before disposal
            try
            {
                process.CancelOutputRead();
                process.CancelErrorRead();
            }
            catch
            {
                // Best effort - ignore errors during cleanup
            }
        }
    }

    private async Task<bool> ConvertToChdAsync(string chdmanPath, string inputFile, string outputFile, int cores, bool forceCd, bool forceDvd, int? timeoutMinutes, CancellationToken token)
    {
        if (!File.Exists(chdmanPath))
        {
            LogMessage($"ERROR: chdman.exe not found at '{chdmanPath}'. Please verify the path in settings.");
            return false;
        }

        using var process = new Process();

        var isImg = inputFile.EndsWith(FileExtensions.Img, StringComparison.OrdinalIgnoreCase);
        var isCcd = inputFile.EndsWith(FileExtensions.Ccd, StringComparison.OrdinalIgnoreCase);
        var hasCcd = isImg && File.Exists(Path.ChangeExtension(inputFile, FileExtensions.Ccd));

        var command = forceCd || isCcd || hasCcd || (!forceDvd && !inputFile.EndsWith(FileExtensions.Iso, StringComparison.OrdinalIgnoreCase) && !isImg && !inputFile.EndsWith(FileExtensions.Raw, StringComparison.OrdinalIgnoreCase))
            ? "createcd"
            : forceDvd || inputFile.EndsWith(FileExtensions.Iso, StringComparison.OrdinalIgnoreCase)
                ? "createdvd"
                : isImg
                    ? "createhd"
                    : "createraw";

        var args = $"{command} -i \"{inputFile}\" -o \"{outputFile}\" -f -np {cores}";
        LogMessage($"CHDMAN: {command} {Path.GetFileName(inputFile)}");

        process.StartInfo = new ProcessStartInfo
        {
            FileName = chdmanPath,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            ErrorDialog = false
        };

        var errorBuffer = new StringBuilder();
        process.OutputDataReceived += (_, a) =>
        {
            if (string.IsNullOrEmpty(a.Data)) return;

            if (a.Data.Contains("Compression complete") || a.Data.Contains("final ratio"))
            {
                LogMessage($"[CHDMAN ✓] {a.Data}");
            }
            else if (!a.Data.Contains("% complete") &&
                     !a.Data.Contains("Compressing") &&
                     !a.Data.Contains("Output bytes") &&
                     !a.Data.Contains("Compression ratio"))
            {
                LogMessage($"[CHDMAN] {a.Data}");
            }
        };

        process.ErrorDataReceived += (_, a) =>
        {
            if (string.IsNullOrEmpty(a.Data)) return;

            errorBuffer.AppendLine(a.Data);

            if (a.Data.Contains("Compression complete") || a.Data.Contains("final ratio"))
            {
                LogMessage($"[CHDMAN ✓] {a.Data}");
            }
            else if (!a.Data.Contains("% complete") &&
                     !a.Data.Contains("Compressing") &&
                     !a.Data.Contains("Output bytes") &&
                     !a.Data.Contains("Compression ratio"))
            {
                LogMessage($"[CHDMAN] {a.Data}");
            }
        };

        using var ctsSpeed = CancellationTokenSource.CreateLinkedTokenSource(token);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var speedToken = ctsSpeed.Token;

        var speedMonitoringTask = Task.Run(async () =>
        {
            try
            {
                while (!speedToken.IsCancellationRequested)
                {
                    UpdateWriteSpeedFromPerformanceCounter();
                    await Task.Delay(AppConfig.WriteSpeedUpdateIntervalMs, speedToken);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, speedToken);

        try
        {
            token.ThrowIfCancellationRequested();

            if (timeoutMinutes is > 0)
            {
                using var timeoutCts = new CancellationTokenSource();
                timeoutCts.CancelAfter(TimeSpan.FromMinutes(timeoutMinutes.Value));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutCts.Token);

                await process.WaitForExitAsync(linkedCts.Token);
            }
            else
            {
                await process.WaitForExitAsync(token);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException)
        {
            if (token.IsCancellationRequested)
                throw;

            if (timeoutMinutes != null) LogMessage($"TIMEOUT: Conversion of '{Path.GetFileName(inputFile)}' exceeded {timeoutMinutes.Value} minute(s). Marking as failed.");
            return false;
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(true);
                await Task.Run(() => process.WaitForExit(5000), CancellationToken.None);
            }

            ctsSpeed.Cancel();
            await Task.WhenAny(speedMonitoringTask, Task.Delay(500, CancellationToken.None));
            process.CancelOutputRead();
            process.CancelErrorRead();
        }

        if (process.ExitCode == 0 && !token.IsCancellationRequested)
            return true;

        if (!token.IsCancellationRequested && IsDiskSpaceError(errorBuffer.ToString()))
        {
            LogMessage($"ERROR: Conversion of '{Path.GetFileName(inputFile)}' failed due to insufficient disk space.");
            LogMessage("       Free up disk space on the output drive and try again.");
        }

        return false;
    }

    private async Task<PbpExtractionResult> ExtractPbpToCueBinAsync(string psxPackagerPath, string inputFile, string outputFolder, CancellationToken token)
    {
        using var process = new Process();
        var args = $"-i \"{inputFile}\" -o \"{outputFolder}\" -x";
        LogMessage($"PSXPACKAGER: Extracting {Path.GetFileName(inputFile)}");

        // Use a hidden window instead of no window to provide a valid console handle
        // This prevents PSXPackager from crashing when it tries to set Console.CursorVisible
        process.StartInfo = new ProcessStartInfo
        {
            FileName = psxPackagerPath,
            Arguments = args,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            UseShellExecute = false,
            CreateNoWindow = false,
            WindowStyle = ProcessWindowStyle.Hidden,
            ErrorDialog = false
        };

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeoutCts.CancelAfter(TimeSpan.FromHours(AppConfig.MaxConversionTimeoutHours));

        process.Start();

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
            if (token.IsCancellationRequested && !process.HasExited)
            {
                throw new OperationCanceledException();
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(true);
                await Task.Run(() => process.WaitForExit(5000), CancellationToken.None);
            }
        }

        if (process.ExitCode != 0)
        {
            LogMessage($"PSXPACKAGER: Extraction failed with exit code {process.ExitCode}");
            return new PbpExtractionResult { Success = false };
        }

        // Find all generated CUE files in the output folder
        var cueFiles = Directory.GetFiles(outputFolder, "*.cue");
        if (cueFiles.Length == 0)
        {
            LogMessage("PSXPACKAGER: No CUE file found after extraction");
            return new PbpExtractionResult { Success = false };
        }

        // Return all CUE files (multi-disc PBP files produce multiple CUE files)
        LogMessage($"PSXPACKAGER: Extracted {cueFiles.Length} disc(s)");

        return new PbpExtractionResult
        {
            Success = true,
            CueFilePaths = cueFiles.ToList(),
            OutputFolder = outputFolder
        };
    }

    private void UpdateWriteSpeedFromPerformanceCounter()
    {
        try
        {
            double writeBytesPerSec;
            lock (_performanceCounterLock)
            {
                writeBytesPerSec = _writeBytesCounter?.NextValue() ?? 0;
            }

            if (writeBytesPerSec > 0)
            {
                UpdateWriteSpeedDisplay(writeBytesPerSec / 1048576.0); // Convert to MB/s
            }
        }
        catch
        {
            // Ignore performance counter errors
        }
    }

    private void UpdateReadSpeedFromPerformanceCounter()
    {
        try
        {
            double readBytesPerSec;
            lock (_performanceCounterLock)
            {
                readBytesPerSec = _readBytesCounter?.NextValue() ?? 0;
            }

            if (readBytesPerSec > 0)
            {
                UpdateReadSpeedDisplay(readBytesPerSec / 1048576.0); // Convert to MB/s
            }
        }
        catch
        {
            // ignored
        }
    }

    private async Task<bool> VerifyChdAsync(string chdmanPath, string chdFile, CancellationToken token)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = chdmanPath,
            Arguments = $"verify -i \"{chdFile}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(chdmanPath),
            ErrorDialog = false
        };

        // chdman often sends progress to Standard Error
        process.OutputDataReceived += (_, a) =>
        {
            if (string.IsNullOrEmpty(a.Data)) return;

            // Trigger speed update when progress is reported
            if (a.Data.Contains("% complete"))
            {
                UpdateReadSpeedFromPerformanceCounter();
            }
            else
            {
                LogMessage($"[CHDMAN] {a.Data}");
            }
        };

        process.ErrorDataReceived += (_, a) =>
        {
            if (string.IsNullOrEmpty(a.Data)) return;

            // Trigger speed update when progress is reported in Error stream
            if (a.Data.Contains("% complete"))
            {
                UpdateReadSpeedFromPerformanceCounter();
            }
            else
            {
                LogMessage($"[CHDMAN] {a.Data}");
            }
        };

        using var ctsSpeed = CancellationTokenSource.CreateLinkedTokenSource(token);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var speedToken = ctsSpeed.Token;
        var readSpeedTask = Task.Run(async () =>
        {
            try
            {
                // Initial delay to let the process start reading
                await Task.Delay(100, speedToken);
                while (!speedToken.IsCancellationRequested)
                {
                    UpdateReadSpeedFromPerformanceCounter();
                    await Task.Delay(AppConfig.WriteSpeedUpdateIntervalMs, speedToken);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, speedToken);

        try
        {
            await process.WaitForExitAsync(token);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(true);
                await Task.Run(() => process.WaitForExit(5000), CancellationToken.None);
            }

            ctsSpeed.Cancel();
            await Task.WhenAny(readSpeedTask, Task.Delay(500, CancellationToken.None));
            // Ensure output streams are properly drained before disposal
            process.CancelOutputRead();
            process.CancelErrorRead();
        }

        return process.ExitCode == 0;
    }

    private void ResetOperationStats()
    {
        _totalFilesProcessed = 0;
        _processedOkCount = 0;
        _failedCount = 0;
        _operationTimer.Reset();
        UpdateStatsDisplay();
        UpdateProcessingTimeDisplay();
        ResetSpeedCounters();
        ClearProgressDisplay();
    }

    private void UpdateStatsDisplay()
    {
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            TotalFilesValue.Text = $"{_totalFilesProcessed}";
            SuccessValue.Text = $"{_processedOkCount}";
            FailedValue.Text = $"{_failedCount}";
        });
    }

    private void UpdateProcessingTimeDisplay()
    {
        Application.Current.Dispatcher.InvokeAsync(() => ProcessingTimeValue.Text = $@"{_operationTimer.Elapsed:hh\:mm\:ss}");
    }

    private void UpdateWriteSpeedDisplay(double speed)
    {
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            // Update the actual label
            SpeedValue.Text = $"{speed:F1} MB/s";

            if (speed > 0 && !StartConversionButton.IsEnabled)
            {
                StatusBarMessage.Text = "Converting...";
            }
        });
    }

    private void UpdateReadSpeedDisplay(double speed)
    {
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            SpeedValue.Text = $"{speed:F1} MB/s";
            StatusBarMessage.Text = speed switch
            {
                > 0 when !StartExtractionButton.IsEnabled => "Extracting...",
                > 0 when !StartVerificationButton.IsEnabled => "Verifying...",
                _ => StatusBarMessage.Text
            };
        });
    }

    private void UpdateProgressDisplay(int completedCount, int tot, string name, string verb)
    {
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            // If we haven't finished all files, show the next one in the text (completed + 1)
            var displayIndex = Math.Min(completedCount + 1, tot);
            ProgressText.Text = completedCount < tot
                ? $"{verb} {displayIndex}/{tot}: {name}"
                : $"{verb} process complete.";

            ProgressBar.IsIndeterminate = false;
            ProgressBar.Value = completedCount;
            ProgressBar.Maximum = tot > 0 ? tot : 1;
            ProgressText.Visibility = Visibility.Visible;
            ProgressBar.Visibility = Visibility.Visible;
        });
    }

    private void ClearProgressDisplay()
    {
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            ProgressBar.Value = 0;
            ProgressBar.Visibility = Visibility.Collapsed;
            ProgressText.Text = "";
            ProgressText.Visibility = Visibility.Collapsed;
        });
    }

    private async Task DeleteOriginalGameFilesAsync(string inputFile, CancellationToken token)
    {
        try
        {
            var files = new List<string> { inputFile };
            var ext = Path.GetExtension(inputFile);
            if (ext.Equals(FileExtensions.Cue, StringComparison.OrdinalIgnoreCase))
            {
                files.AddRange(await GameFileParser.GetReferencedFilesFromCueAsync(inputFile, LogMessage, token));
            }
            else if (ext.Equals(FileExtensions.Gdi, StringComparison.OrdinalIgnoreCase))
            {
                files.AddRange(await GameFileParser.GetReferencedFilesFromGdiAsync(inputFile, LogMessage, token));
            }
            else if (ext.Equals(FileExtensions.Toc, StringComparison.OrdinalIgnoreCase))
            {
                files.AddRange(await GameFileParser.GetReferencedFilesFromTocAsync(inputFile, LogMessage, token));
            }
            else if (ext.Equals(FileExtensions.Ccd, StringComparison.OrdinalIgnoreCase))
            {
                // CloneCD references: .img, .sub
                var imgFile = Path.ChangeExtension(inputFile, FileExtensions.Img);
                var subFile = Path.ChangeExtension(inputFile, FileExtensions.Sub);
                if (File.Exists(imgFile)) files.Add(imgFile);
                if (File.Exists(subFile)) files.Add(subFile);
            }

            foreach (var f in files.Distinct()) await TryDeleteFileAsync(f, "game file", token);
        }
        catch (Exception ex)
        {
            LogMessage($"Delete error: {ex.Message}");
            _ = ReportBugAsync("Delete error", ex);
        }
    }

    private static async Task CopyFileWithRetryAsync(string source, string dest, CancellationToken token)
    {
        const int maxRetries = 3;
        const int baseDelayMs = 500;

        for (var attempt = 0; attempt < maxRetries; attempt++)
        {
            token.ThrowIfCancellationRequested();

            try
            {
                await Task.Run(() => File.Copy(source, dest, true), token);
                return;
            }
            catch (IOException ex) when (attempt < maxRetries - 1 && !IsDiskSpaceException(ex) && !IsCrcErrorException(ex))
            {
                await Task.Delay(baseDelayMs * (1 << attempt), token);
            }
        }
    }

    internal static bool IsCancellationException(Exception ex)
    {
        return ex is OperationCanceledException;
    }

    internal static bool IsDiskSpaceException(Exception ex)
    {
        // HResult 0x80070070 = ERROR_DISK_FULL, 0x80070079 = ERROR_SEM_TIMEOUT (can indicate disk issues)
        return ex is IOException { HResult: -2147024784 or -2147024783 };
    }

    internal static bool IsCrcErrorException(Exception ex)
    {
        // HResult 0x80070017 = ERROR_CRC (cyclic redundancy check)
        return ex is IOException { HResult: -2147024809 };
    }

    internal static bool IsCorruptionException(Exception ex)
    {
        return ex is InvalidDataException
                   or IndexOutOfRangeException
                   or NullReferenceException
                   or System.Security.Cryptography.CryptographicException
               || ex.GetType().FullName is
                   "SharpCompress.Common.IncompleteArchiveException"
                   or "SharpCompress.Common.ArchiveOperationException"
                   or "SharpCompress.Common.InvalidFormatException"
                   or "SharpCompress.Compressors.LZMA.DataErrorException";
    }

    private static bool IsDiskSpaceError(string? errorOutput)
    {
        if (string.IsNullOrEmpty(errorOutput)) return false;

        return errorOutput.Contains("not enough space", StringComparison.OrdinalIgnoreCase) ||
               errorOutput.Contains("not enough disk space", StringComparison.OrdinalIgnoreCase) ||
               errorOutput.Contains("disk full", StringComparison.OrdinalIgnoreCase) ||
               errorOutput.Contains("no space left", StringComparison.OrdinalIgnoreCase) ||
               errorOutput.Contains("insufficient disk space", StringComparison.OrdinalIgnoreCase);
    }

    private void CheckDiskSpace(string outputFolder, string[] filesToProcess, bool isConversion)
    {
        try
        {
            var outputRoot = Path.GetPathRoot(Path.GetFullPath(outputFolder));
            if (string.IsNullOrEmpty(outputRoot)) return;

            var driveInfo = new DriveInfo(outputRoot);
            if (!driveInfo.IsReady) return;

            var availableGb = driveInfo.AvailableFreeSpace / (1024.0 * 1024.0 * 1024.0);
            var totalInputSize = 0L;
            foreach (var file in filesToProcess)
            {
                try
                {
                    totalInputSize += new FileInfo(file).Length;
                }
                catch
                {
                    /* skip inaccessible files */
                }
            }

            var totalInputGb = totalInputSize / (1024.0 * 1024.0 * 1024.0);

            if (isConversion)
            {
                // CHD compression typically reduces size, but warn if available space < 50% of input
                if (availableGb < totalInputGb * 0.5)
                {
                    LogMessage($"WARNING: Output drive ({outputRoot.TrimEnd('\\')}) has {availableGb:F1} GB free, input files total {totalInputGb:F1} GB.");
                    LogMessage("         CHD compression usually reduces file size, but you may run out of disk space.");
                }
            }
            else
            {
                // Extraction: output can be larger than CHD input
                if (availableGb < totalInputGb)
                {
                    LogMessage($"WARNING: Output drive ({outputRoot.TrimEnd('\\')}) has {availableGb:F1} GB free, CHD files total {totalInputGb:F1} GB.");
                    LogMessage("         Extracted files are typically larger than CHD files. You may run out of disk space.");
                }
            }

            // Also check temp drive if conversion (temp files are created)
            if (isConversion)
            {
                var tempRoot = Path.GetPathRoot(Path.GetTempPath());
                if (!string.IsNullOrEmpty(tempRoot) && !string.Equals(tempRoot, outputRoot, StringComparison.OrdinalIgnoreCase))
                {
                    var tempDrive = new DriveInfo(tempRoot);
                    if (tempDrive.IsReady)
                    {
                        var tempFreeGb = tempDrive.AvailableFreeSpace / (1024.0 * 1024.0 * 1024.0);
                        if (tempFreeGb < totalInputGb)
                        {
                            LogMessage($"WARNING: Temp drive ({tempRoot.TrimEnd('\\')}) has {tempFreeGb:F1} GB free, input files total {totalInputGb:F1} GB.");
                            LogMessage("         Temporary files are created during conversion. You may run out of disk space.");
                        }
                    }
                }
            }
        }
        catch
        {
            // Best effort - don't fail the operation if disk check itself fails
        }
    }

    private async Task TryDeleteFileAsync(string path, string desc, CancellationToken token)
    {
        try
        {
            await Task.Run(() => File.Delete(path), token);
            LogMessage($"Deleted {desc}: {Path.GetFileName(path)}");
        }
        catch (FileNotFoundException)
        {
            // File already deleted - this is acceptable
            LogMessage($"{desc} already deleted: {Path.GetFileName(path)}");
        }
        catch
        {
            LogMessage($"Failed to delete {desc}: {Path.GetFileName(path)}");
        }
    }

    private async Task TryDeleteDirectoryAsync(string path, string desc, CancellationToken token)
    {
        try
        {
            await Task.Run(() => Directory.Delete(path, true), token);
        }
        catch (DirectoryNotFoundException)
        {
            // Directory already deleted - this is acceptable
            LogMessage($"{desc} already deleted: {Path.GetFileName(path)}");
        }
        catch
        {
            LogMessage($"Failed to delete {desc}: {path}");
        }
    }

    private async Task TryDeleteEmptySubfolderAsync(string subfolderPath, string inputFolder, CancellationToken token)
    {
        try
        {
            // Don't delete the root input folder
            if (string.Equals(Path.GetFullPath(subfolderPath), Path.GetFullPath(inputFolder), StringComparison.OrdinalIgnoreCase))
                return;

            if (Directory.Exists(subfolderPath) && !Directory.EnumerateFileSystemEntries(subfolderPath).Any())
            {
                await Task.Run(() => Directory.Delete(subfolderPath, false), token);
                LogMessage($"Deleted empty folder: {Path.GetFileName(subfolderPath)}");
            }
        }
        catch
        {
            // Ignore folder deletion errors
        }
    }

    private void SearchSubfoldersCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded)
        {
            RefreshFileListForActiveTab();
        }
    }

    private void ForceCreateCdCheckBox_Checked(object sender, RoutedEventArgs e)
    {
        ForceCreateDvdCheckBox.IsChecked = false;
    }

    private void ForceCreateDvdCheckBox_Checked(object sender, RoutedEventArgs e)
    {
        ForceCreateCdCheckBox.IsChecked = false;
    }

    private void LogOperationSummary(string op)
    {
        LogMessage($"--- {op} completed. Total: {_totalFilesProcessed}, OK: {_processedOkCount}, Failed: {_failedCount}");
        UpdateStatusBarMessage($"{op} completed" + (_failedCount > 0 ? " with errors" : ""));
        ShowMessageBox($"{op} completed.\nTotal: {_totalFilesProcessed}\nOK: {_processedOkCount}\nFailed: {_failedCount}", "Complete", MessageBoxButton.OK, _failedCount > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }

    private void ShowMessageBox(string msg, string title, MessageBoxButton btns, MessageBoxImage icon)
    {
        MessageBox.Show(this, msg, title, btns, icon);
    }

    private void ShowError(string msg)
    {
        Application.Current.Dispatcher.InvokeAsync(() => ShowMessageBox(msg, "Error", MessageBoxButton.OK, MessageBoxImage.Error));
    }

    private static async Task ReportBugAsync(string msg, Exception? ex = null)
    {
        try
        {
            if (App.SharedBugReportService != null)
            {
                await App.SharedBugReportService.SendBugReportAsync(msg, ex);
            }
        }
        catch
        {
            // ignored
        }
    }

    private void ResetSpeedCounters()
    {
        // Reset performance counters to get fresh readings
        lock (_performanceCounterLock)
        {
            _writeBytesCounter?.NextValue();
            _readBytesCounter?.NextValue();
        }
    }

    private void ExitMenuItem_Click(object sender, RoutedEventArgs e)
    {
        // Ensure the window close process is initiated
        // The Window_Closing event will handle proper cleanup and shutdown
        Close();
    }

    private void AboutMenuItem_Click(object sender, RoutedEventArgs e)
    {
        new AboutWindow { Owner = this }.ShowDialog();
    }

    /// <summary>
    /// Releases all resources used by the <see cref="MainWindow"/>.
    /// Cancels ongoing operations and disposes managed resources.
    /// </summary>
    public void Dispose()
    {
        lock (_ctsLock)
        {
            _cts.Cancel();
            _cts.Dispose();
        }

        _writeBytesCounter?.Dispose();
        _readBytesCounter?.Dispose();
        _archiveService.Dispose();
        _operationTimer.Stop();

        KillOrphanedProcesses();
        GC.SuppressFinalize(this);
    }

    private static void KillOrphanedProcesses()
    {
        try
        {
            var currentProcessId = Environment.ProcessId;
            var toolNames = new[] { "chdman", "maxcso", "7za", AppConfig.PsxPackagerExeName };

            foreach (var toolName in toolNames)
            {
                try
                {
                    var processes = Process.GetProcessesByName(
                        Path.GetFileNameWithoutExtension(toolName));
                    foreach (var process in processes)
                    {
                        try
                        {
                            if (process.Id != currentProcessId)
                            {
                                process.Kill(true);
                                process.WaitForExit(3000);
                            }
                        }
                        catch
                        {
                            // Process already exited or access denied
                        }
                    }
                }
                catch
                {
                    // Process name not found or access denied
                }
            }
        }
        catch
        {
            // Best-effort cleanup
        }
    }
}