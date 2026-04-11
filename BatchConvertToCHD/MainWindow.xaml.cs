using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using BatchConvertToCHD.Models;
using BatchConvertToCHD.Services;
using BatchConvertToCHD.Utilities;

namespace BatchConvertToCHD;

public partial class MainWindow : IDisposable
{
    private CancellationTokenSource _cts;
    private readonly string _maxCsoPath;
    private readonly bool _isMaxCsoAvailable;
    private readonly bool _isChdmanAvailable;
    private readonly string _psxPackagerPath;
    private readonly bool _isPsxPackagerAvailable;

    private static readonly string[] AllSupportedInputExtensionsForConversion = [".cue", ".iso", ".img", ".cdi", ".gdi", ".toc", ".raw", ".zip", ".7z", ".rar", ".cso", ".pbp", ".ccd"];
    private static readonly string[] ArchiveExtensions = [".zip", ".7z", ".rar"];

    // Statistics
    private int _totalFilesProcessed;
    private int _processedOkCount;
    private int _failedCount;
    private readonly Stopwatch _operationTimer = new();

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
    private PerformanceCounter? _writeBytesCounter;
    private PerformanceCounter? _readBytesCounter;
    private readonly object _performanceCounterLock = new();

    // Result class for PBP extraction
    private class PbpExtractionResult
    {
        public bool Success { get; set; }
        public List<string> CueFilePaths { get; set; } = new();
        public string? OutputFolder { get; set; }
    }

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

        _maxCsoPath = Path.Combine(appDirectory, "maxcso.exe");
        _isMaxCsoAvailable = File.Exists(_maxCsoPath) && !AppConfig.IsArm64;

        _psxPackagerPath = Path.Combine(appDirectory, AppConfig.PsxPackagerExeName);
        _isPsxPackagerAvailable = File.Exists(_psxPackagerPath);

        // Initialize Services
        _updateService = new UpdateService(AppConfig.ApplicationName);
        _archiveService = new ArchiveService(_maxCsoPath, _isMaxCsoAvailable);

        InitializeStatusBar();
        Task.Run(static async () =>
        {
            await Task.Delay(2000);
            CleanupLeftoverTempDirectories();
        });
        DisplayConversionInstructionsInLog();
        ResetOperationStats();
        LogEnvironmentDetails();

        // Defer heavy initialization until after window is shown
        Loaded += MainWindow_Loaded;

        // Hide speed display initially until we know counters are available
        SpeedStatCard.Visibility = Visibility.Collapsed;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            // Initialize performance counters asynchronously
            await Task.Run(() =>
            {
                InitializePerformanceCounter();
                InitializeReadPerformanceCounter();
            });

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
        if (!_isChdmanAvailable) missingDeps.Add(AppConfig.ChdmanExeName);

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
        if (!_isMaxCsoAvailable && !AppConfig.IsArm64) missingOptional.Add("maxcso.exe (required for .cso files)");
        if (!_isPsxPackagerAvailable) missingOptional.Add(AppConfig.PsxPackagerExeName + " (required for .pbp files)");

        if (missingOptional.Count > 0)
        {
            LogMessage("NOTE: Some optional components are missing: " + string.Join(", ", missingOptional));
        }
    }

    private void InitializePerformanceCounter()
    {
        try
        {
            // Check if category exists first to avoid registry errors
            if (!PerformanceCounterCategory.Exists("PhysicalDisk"))
            {
                LogMessage("WARNING: PhysicalDisk performance counter category not available");
                _writeBytesCounter = null;
                return;
            }

            // Create a performance counter for disk write operations
            _writeBytesCounter = new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", "_Total");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("invalid index") ||
                                                   ex.Message.Contains("registry") ||
                                                   ex.Message.Contains("Cannot load Counter Name"))
        {
            // System configuration issue - don't report as bug
            LogMessage("WARNING: Performance counters unavailable due to system registry issue");
            _writeBytesCounter = null;
        }
        catch (Exception ex)
        {
            LogMessage($"WARNING: Could not initialize performance counter for write speed monitoring: {ex.Message}");
            _writeBytesCounter = null;
        }
    }

    private void InitializeReadPerformanceCounter()
    {
        try
        {
            // Check if category exists first to avoid registry errors
            if (!PerformanceCounterCategory.Exists("PhysicalDisk"))
            {
                _readBytesCounter = null;
                return;
            }

            // Create a performance counter for disk read operations
            _readBytesCounter = new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", "_Total");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("invalid index") ||
                                                   ex.Message.Contains("registry") ||
                                                   ex.Message.Contains("Cannot load Counter Name"))
        {
            // System configuration issue - don't report as bug
            _readBytesCounter = null;
        }
        catch (Exception ex)
        {
            LogMessage($"WARNING: Could not initialize read performance counter: {ex.Message}");
            _readBytesCounter = null;
        }
    }

    private void InitializeStatusBar()
    {
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            StatusBarChdman.Text = " CHDMAN ";
            StatusBarChdman.Foreground = _isChdmanAvailable ? new SolidColorBrush(Colors.Green) : new SolidColorBrush(Colors.Red);
            StatusBarMaxcso.Text = " MAXCSO ";
            StatusBarMaxcso.Foreground = _isMaxCsoAvailable ? new SolidColorBrush(Colors.Green) : new SolidColorBrush(Colors.Orange);
            StatusBarPsxPackager.Text = " PSXPACKAGER ";
            StatusBarPsxPackager.Foreground = _isPsxPackagerAvailable ? new SolidColorBrush(Colors.Green) : new SolidColorBrush(Colors.Orange);
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
                var directories = Directory.GetDirectories(Path.GetTempPath(), $"{TempDirPrefix}*");
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

            await using (File.OpenRead(exePath))
            {
            } // Check read access

            return true;
        }
        catch (Exception ex)
        {
            LogMessage($"PERMISSION ERROR: Cannot access {exeName}. {ex.Message}");
            ShowError($"Access denied to {exeName}. Check antivirus or permissions.");
            return false;
        }
    }

    /// <summary>
    /// Validates that chdman.exe is compatible with the current OS platform.
    /// This catches Win32Exception (0x800700C1) when the executable is not valid for this OS.
    /// </summary>
    private async Task<bool> ValidateChdmanCompatibilityAsync(string chdmanPath)
    {
        try
        {
            using var process = new Process();
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
            await process.WaitForExitAsync(CancellationToken.None);
            return true;
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
            // Other errors are acceptable - at least the exe started or we have a generic error
            LogMessage($"WARNING: Could not validate chdman compatibility: {ex.Message}");
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
        if (!_isChdmanAvailable) LogMessage("WARNING: chdman.exe not found!");
        if (!_isMaxCsoAvailable) LogMessage("WARNING: maxcso.exe not found.");
        if (!_isPsxPackagerAvailable) LogMessage("WARNING: psxpackager.exe not found. PBP conversion unavailable.");
        LogMessage("--- Ready for Conversion ---");
    }

    private void DisplayVerificationInstructionsInLog()
    {
        LogMessage($"Welcome to {AppConfig.ApplicationName}. (Verification Mode)");
        if (!_isChdmanAvailable) LogMessage("WARNING: chdman.exe not found!");
        LogMessage("--- Ready for Verification ---");
    }

    private void DisplayExtractionInstructionsInLog()
    {
        LogMessage($"Welcome to {AppConfig.ApplicationName}. (Extraction Mode)");
        if (!_isChdmanAvailable) LogMessage("WARNING: chdman.exe not found!");
        LogMessage("This feature extracts CHD files back to their original format (ISO/BIN/CUE etc.)");
        LogMessage("--- Ready for Extraction ---");
    }

    private void MainTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source is not TabControl tabControl) return;
        if (!StartConversionButton.IsEnabled && !StartVerificationButton.IsEnabled) return;

        Application.Current.Dispatcher.InvokeAsync((Action)(() => LogViewer.Clear()));
        if (tabControl.SelectedItem is TabItem selectedTab)
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
        _cts.Cancel();
        Dispose();
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
                    // Select first half and replace with truncation message
                    LogViewer.SelectionStart = 0;
                    LogViewer.SelectionLength = MaxLogLength / 2;
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

    private async void StartExtractionButton_Click(object sender, RoutedEventArgs e)
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

            // Cancel any existing operations before creating new CTS
            _cts.Cancel();
            _cts.Dispose();
            _cts = new CancellationTokenSource();

            ResetOperationStats();
            SetControlsState(false);
            await Task.Yield();
            _operationTimer.Restart();
            ResetSpeedCounters();

            var deleteOriginal = DeleteOriginalChdCheckBox.IsChecked ?? false;

            LogMessage("--- Starting batch extraction process... ---");

            try
            {
                await PerformBatchExtractionAsync(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, AppConfig.ChdmanExeName),
                    inputFolder, outputFolder, deleteOriginal, selectedFiles, _cts.Token);
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
        if (string.IsNullOrEmpty(folder)) return;

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
        if (string.IsNullOrEmpty(inputFolder) || !Directory.Exists(inputFolder)) return Task.CompletedTask;

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
                .Where(static file => AllSupportedInputExtensionsForConversion.Contains(Path.GetExtension(file).ToLowerInvariant()))
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
        if (string.IsNullOrEmpty(inputFolder) || !Directory.Exists(inputFolder)) return Task.CompletedTask;

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
                    if (!includeSub) return true;

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
        if (string.IsNullOrEmpty(inputFolder) || !Directory.Exists(inputFolder)) return Task.CompletedTask;

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
                    if (!includeSub) return true;

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

    private async void StartConversionButton_Click(object sender, RoutedEventArgs e)
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
            if (inputFolder == null || outputFolder == null) return;

            if (inputFolder.Equals(outputFolder, StringComparison.OrdinalIgnoreCase))
            {
                ShowError("Input and output folders must be different.");
                return;
            }

            // Cancel any existing operations before creating new CTS
            // The old CTS will be disposed after the operation completes in finally block
            _cts.Cancel();
            _cts.Dispose();
            _cts = new CancellationTokenSource();

            ResetOperationStats();
            SetControlsState(false);
            await Task.Yield();
            _operationTimer.Restart();
            ResetSpeedCounters();

            var deleteFiles = DeleteOriginalsCheckBox.IsChecked ?? false;
            var processSmallerFirst = ProcessSmallerFirstCheckBox.IsChecked ?? false;
            var forceCd = ForceCreateCdCheckBox.IsChecked ?? false;
            var forceDvd = ForceCreateDvdCheckBox.IsChecked ?? false;

            var selectedFiles = _conversionFiles.Where(static f => f.IsSelected).Select(static f => f.FullPath).ToArray();
            if (selectedFiles.Length == 0)
            {
                ShowError("No files selected for conversion.");
                return;
            }

            LogMessage("--- Starting batch conversion process... ---");

            try
            {
                await PerformBatchConversionAsync(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, AppConfig.ChdmanExeName),
                    inputFolder, outputFolder, deleteFiles, processSmallerFirst, forceCd, forceDvd, selectedFiles, _cts.Token);
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

    private async void StartVerificationButton_Click(object sender, RoutedEventArgs e)
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
            if (inputFolder == null) return;

            // Cancel any existing operations before creating new CTS
            // The old CTS will be disposed after the operation completes in finally block
            _cts.Cancel();
            _cts.Dispose();
            _cts = new CancellationTokenSource();

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
                await PerformBatchVerificationAsync(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, AppConfig.ChdmanExeName),
                    inputFolder, includeSubfolders, moveSuccess, successFolder, moveFailed, failedFolder, selectedFiles, _cts.Token);
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
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _cts.Cancel();
        LogMessage("Cancellation requested...");
        UpdateStatusBarMessage("Cancelling...");
    }

    private void SetControlsState(bool enabled)
    {
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

    private async Task PerformBatchConversionAsync(string chdmanPath, string inputFolder, string outputFolder, bool deleteFiles, bool processSmallerFirst, bool forceCd, bool forceDvd, string[] selectedFiles, CancellationToken token)
    {
        if (!await ValidateExecutableAccessAsync(chdmanPath, "chdman.exe")) return;

        if (!await ValidateChdmanCompatibilityAsync(chdmanPath)) return;

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
        if (_totalFilesProcessed == 0) return;

        await Application.Current.Dispatcher.InvokeAsync(() => ProgressBar.Maximum = _totalFilesProcessed);
        var processedCount = 0;
        var cores = Environment.ProcessorCount;
        ResetSpeedCounters();

        foreach (var file in filesToConvert)
        {
            token.ThrowIfCancellationRequested();

            // Update text to show we are starting this file, but bar stays at 'processedCount'
            UpdateProgressDisplay(processedCount, _totalFilesProcessed, Path.GetFileName(file), "Converting");

            var success = await ProcessSingleFileForConversionAsync(chdmanPath, file, inputFolder, outputFolder, deleteFiles, cores, forceCd, forceDvd, token);
            if (success)
            {
                _processedOkCount++;
            }
            else
            {
                _failedCount++;
            }

            processedCount++;
            UpdateProgressDisplay(processedCount, _totalFilesProcessed, "Finishing...", "Converting");
            UpdateStatsDisplay();
            UpdateProcessingTimeDisplay();
            UpdateWriteSpeedFromPerformanceCounter();
        }
    }

    private async Task<bool> ProcessSingleFileForConversionAsync(string chdmanPath, string inputFile, string inputFolder, string outputFolder, bool deleteOriginal, int cores, bool forceCd, bool forceDvd, CancellationToken token)
    {
        var originalName = Path.GetFileName(inputFile);
        LogMessage($"Processing: {originalName}");
        var ext = Path.GetExtension(inputFile).ToLowerInvariant();
        var tempDirs = new List<string>();
        var outputChd = string.Empty;

        try
        {
            token.ThrowIfCancellationRequested();
            var chdBase = Path.GetFileNameWithoutExtension(originalName);

            // Maintain directory structure if searching subfolders
            var relativePath = Path.GetRelativePath(inputFolder, Path.GetDirectoryName(inputFile) ?? inputFolder);
            var targetDir = relativePath == "." ? outputFolder : Path.Combine(outputFolder, relativePath);

            outputChd = Path.Combine(targetDir, PathUtils.SanitizeFileName(chdBase) + ".chd");

            string fileToProcess;
            if (ext == ".cso")
            {
                var tempDir = Path.Combine(Path.GetTempPath(), $"{TempDirPrefix}{Guid.NewGuid():N}");
                tempDirs.Add(tempDir);
                await Task.Run(() => Directory.CreateDirectory(tempDir), token);
                var tempIso = PathUtils.GetSafeTempFileName(originalName, "iso", tempDir);

                var result = await _archiveService.ExtractCsoAsync(inputFile, tempIso, tempDir, LogMessage, UpdateWriteSpeedDisplay, token);
                if (!result.Success) return false;

                fileToProcess = result.FilePath;
            }
            else if (ArchiveExtensions.Contains(ext))
            {
                var tempDir = Path.Combine(Path.GetTempPath(), $"{TempDirPrefix}{Guid.NewGuid():N}");
                tempDirs.Add(tempDir);
                var result = await _archiveService.ExtractArchiveAsync(inputFile, tempDir, LogMessage, token);
                if (!result.Success) return false;

                // Convert all supported files found in the archive
                var allSucceeded = true;
                foreach (var extractedFile in result.FilePaths)
                {
                    token.ThrowIfCancellationRequested();
                    var extractedFileOutputChd = ComputeOutputChdPathForExtractedFile(extractedFile, inputFile, inputFolder, outputFolder);
                    var extractedOutputDir = Path.GetDirectoryName(extractedFileOutputChd) ?? outputFolder;
                    if (!Directory.Exists(extractedOutputDir)) Directory.CreateDirectory(extractedOutputDir);

                    LogMessage($"Converting extracted file: {Path.GetFileName(extractedFile)}");
                    var converted = await ConvertToChdAsync(chdmanPath, extractedFile, extractedFileOutputChd, cores, forceCd, forceDvd, token);
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
            else if (ext == ".pbp")
            {
                if (!_isPsxPackagerAvailable)
                {
                    LogMessage($"ERROR: Cannot process {originalName}. psxpackager.exe not found.");
                    return false;
                }

                var tempDir = Path.Combine(Path.GetTempPath(), $"{TempDirPrefix}{Guid.NewGuid():N}");
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

                    var converted = await ConvertToChdAsync(chdmanPath, cueFile, cueFileOutputChd, cores, forceCd, forceDvd, token);
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

            UpdateWriteSpeedDisplay(0);
            var outputDir = Path.GetDirectoryName(outputChd) ?? outputFolder;
            if (!Directory.Exists(outputDir)) Directory.CreateDirectory(outputDir);

            var success = false;
            try
            {
                success = await ConvertToChdAsync(chdmanPath, fileToProcess, outputChd, cores, forceCd, forceDvd, token);
            }
            catch (Exception ex)
            {
                LogMessage($"Direct conversion attempt error for {originalName}: {ex.Message}");
            }

            // Fallback: If direct conversion failed and we haven't already extracted to temp (i.e. it was a direct file attempt),
            // try copying to temp and converting there. This handles network path issues or file locking quirks.
            if (!success && fileToProcess == inputFile && !token.IsCancellationRequested)
            {
                LogMessage($"Direct conversion failed for {originalName}. Retrying via temporary directory copy...");
                await TryDeleteFileAsync(outputChd, "failed partial CHD", CancellationToken.None);

                try
                {
                    var tempDir = Path.Combine(Path.GetTempPath(), $"{TempDirPrefix}{Guid.NewGuid():N}");
                    tempDirs.Add(tempDir);
                    await Task.Run(() => Directory.CreateDirectory(tempDir), token);

                    string tempInputFile;

                    if (ext is ".cue" or ".gdi" or ".toc" or ".ccd")
                    {
                        LogMessage("Copying game with dependencies to temporary directory...");
                        var filesToCopy = new List<string> { inputFile };
                        switch (ext)
                        {
                            case ".cue":
                                filesToCopy.AddRange(await GameFileParser.GetReferencedFilesFromCueAsync(inputFile, LogMessage, token));
                                break;
                            case ".gdi":
                                filesToCopy.AddRange(await GameFileParser.GetReferencedFilesFromGdiAsync(inputFile, LogMessage, token));
                                break;
                            case ".ccd":
                                var imgFile = Path.ChangeExtension(inputFile, ".img");
                                var subFile = Path.ChangeExtension(inputFile, ".sub");
                                if (File.Exists(imgFile)) filesToCopy.Add(imgFile);
                                if (File.Exists(subFile)) filesToCopy.Add(subFile);
                                break;
                            // .toc
                            default:
                                filesToCopy.AddRange(await GameFileParser.GetReferencedFilesFromTocAsync(inputFile, LogMessage, token));
                                break;
                        }

                        foreach (var file in filesToCopy.Distinct())
                        {
                            if (!File.Exists(file))
                            {
                                LogMessage($"WARNING: Referenced file not found, skipping copy: {Path.GetFileName(file)}");
                                continue;
                            }

                            var destPath = Path.Combine(tempDir, Path.GetFileName(file));
                            await Task.Run(() => File.Copy(file, destPath, true), token);
                        }

                        tempInputFile = Path.Combine(tempDir, Path.GetFileName(inputFile));
                    }
                    else
                    {
                        // Original logic for single, non-dependent files
                        tempInputFile = Path.Combine(tempDir, originalName);
                        await Task.Run(() => File.Copy(inputFile, tempInputFile, true), token);
                    }

                    fileToProcess = tempInputFile;
                    success = await ConvertToChdAsync(chdmanPath, fileToProcess, outputChd, cores, forceCd, forceDvd, token);
                }
                catch (Exception ex)
                {
                    LogMessage($"Retry via temp failed for {originalName}: {ex.Message}");
                }
            }

            if (success)
            {
                LogMessage($"Converted: {originalName}");
                if (deleteOriginal)
                {
                    LogMessage($"Deleting source: {originalName} (Option 'Delete originals' is enabled)");

                    var isImgWithCcd = ext == ".img" && File.Exists(Path.ChangeExtension(inputFile, ".ccd"));

                    if (ext is ".cue" or ".gdi" or ".toc" or ".ccd" || isImgWithCcd)
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
            LogMessage($"Error processing {originalName}: {ex.Message}");
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
        var relativePath = Path.GetRelativePath(inputFolder, Path.GetDirectoryName(originalInputFile) ?? inputFolder);
        var targetDir = relativePath == "." ? outputFolder : Path.Combine(outputFolder, relativePath);
        var chdBase = Path.GetFileNameWithoutExtension(extractedFilePath);
        return Path.Combine(targetDir, PathUtils.SanitizeFileName(chdBase) + ".chd");
    }

    private async Task PerformBatchVerificationAsync(string chdmanPath, string inputFolder, bool includeSub, bool moveSuccess, string successFolder, bool moveFailed, string failedFolder, string[] selectedFiles, CancellationToken token)
    {
        if (!await ValidateExecutableAccessAsync(chdmanPath, "chdman.exe")) return;
        if (!await ValidateChdmanCompatibilityAsync(chdmanPath)) return;

        _totalFilesProcessed = selectedFiles.Length;
        UpdateStatsDisplay();
        LogMessage($"Found {_totalFilesProcessed} CHD files.");
        if (_totalFilesProcessed == 0) return;

        if (moveSuccess) Directory.CreateDirectory(successFolder);
        if (moveFailed) Directory.CreateDirectory(failedFolder);

        await Application.Current.Dispatcher.InvokeAsync(() => ProgressBar.Maximum = _totalFilesProcessed);
        var processed = 0;
        ResetSpeedCounters();

        foreach (var file in selectedFiles)
        {
            token.ThrowIfCancellationRequested();

            // Show current file in text, but bar shows 'processed' (completed) count
            UpdateProgressDisplay(processed, _totalFilesProcessed, Path.GetFileName(file), "Verifying");

            var isValid = await VerifyChdAsync(chdmanPath, file, token);

            if (isValid)
            {
                LogMessage($"✓ Verified: {Path.GetFileName(file)}");
                _processedOkCount++;
                if (moveSuccess) await MoveVerifiedFileAsync(file, successFolder, inputFolder, includeSub, "verified", token);
            }
            else
            {
                LogMessage($"✗ Failed: {Path.GetFileName(file)}");
                _failedCount++;
                if (moveFailed) await MoveVerifiedFileAsync(file, failedFolder, inputFolder, includeSub, "failed", token);
            }

            processed++;
            UpdateProgressDisplay(processed, _totalFilesProcessed, "Finishing...", "Verifying");
            UpdateStatsDisplay();
            UpdateProcessingTimeDisplay();
            UpdateReadSpeedFromPerformanceCounter();
        }
    }

    private async Task MoveVerifiedFileAsync(string source, string destParent, string baseInput, bool maintainSub, string reason, CancellationToken token)
    {
        try
        {
            string dest;
            if (maintainSub && Path.GetDirectoryName(source) != baseInput)
            {
                var rel = Path.GetRelativePath(baseInput, Path.GetDirectoryName(source) ?? string.Empty);
                var targetDir = Path.Combine(destParent, rel);
                Directory.CreateDirectory(targetDir);
                dest = Path.Combine(targetDir, Path.GetFileName(source));
            }
            else
            {
                dest = Path.Combine(destParent, Path.GetFileName(source));
            }

            if (File.Exists(dest))
            {
                LogMessage($"Skipping move: {dest} exists.");
                return;
            }

            await Task.Run(() => File.Move(source, dest), token);
            LogMessage($"Moved {Path.GetFileName(source)} ({reason})");
        }
        catch (Exception ex)
        {
            LogMessage($"Move error: {ex.Message}");
        }
    }

    private async Task PerformBatchExtractionAsync(string chdmanPath, string inputFolder, string outputFolder, bool deleteOriginal, string[] selectedFiles, CancellationToken token)
    {
        if (!await ValidateExecutableAccessAsync(chdmanPath, "chdman.exe")) return;
        if (!await ValidateChdmanCompatibilityAsync(chdmanPath)) return;

        _totalFilesProcessed = selectedFiles.Length;
        UpdateStatsDisplay();
        LogMessage($"Found {_totalFilesProcessed} CHD files.");
        if (_totalFilesProcessed == 0) return;

        await Application.Current.Dispatcher.InvokeAsync(() => ProgressBar.Maximum = _totalFilesProcessed);
        var processed = 0;
        ResetSpeedCounters();

        foreach (var file in selectedFiles)
        {
            token.ThrowIfCancellationRequested();

            // Show current file in text, but bar shows 'processed' (completed) count
            UpdateProgressDisplay(processed, _totalFilesProcessed, Path.GetFileName(file), "Extracting");

            var success = await ExtractChdAsync(chdmanPath, file, inputFolder, outputFolder, deleteOriginal, token);

            if (success)
            {
                LogMessage($"✓ Extracted: {Path.GetFileName(file)}");
                _processedOkCount++;
            }
            else
            {
                LogMessage($"✗ Failed: {Path.GetFileName(file)}");
                _failedCount++;
            }

            processed++;
            UpdateProgressDisplay(processed, _totalFilesProcessed, "Finishing...", "Extracting");
            UpdateStatsDisplay();
            UpdateProcessingTimeDisplay();
            UpdateReadSpeedFromPerformanceCounter();
        }
    }

    private async Task<bool> ExtractChdAsync(string chdmanPath, string chdFile, string inputFolder, string outputFolder, bool deleteOriginal, CancellationToken token)
    {
        var fileName = Path.GetFileNameWithoutExtension(chdFile);

        // Maintain directory structure if searching subfolders
        var relativePath = Path.GetRelativePath(inputFolder, Path.GetDirectoryName(chdFile) ?? inputFolder);
        var targetDir = relativePath == "." ? outputFolder : Path.Combine(outputFolder, relativePath);
        if (!Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);

        // Get extraction command based on user-selected output format
        var extractCommand = await GetSelectedExtractCommandAsync(chdmanPath, chdFile, token);

        // Determine output extension based on selection or detected command
        var outputExt = ".cue"; // Default for extractcd
        if (ExtractGdiRadioButton.IsChecked == true)
        {
            outputExt = ".gdi";
        }
        else if (ExtractDvdRadioButton.IsChecked == true)
        {
            outputExt = ".iso";
        }
        else if (ExtractHdRadioButton.IsChecked == true)
        {
            outputExt = ".img";
        }
        else if (ExtractAutoRadioButton.IsChecked == true)
        {
            outputExt = extractCommand switch
            {
                "extractdvd" => ".iso",
                "extracthd" => ".img",
                _ => ".cue"
            };

            if (extractCommand == "extractcd" && await IsGdiChdAsync(chdmanPath, chdFile, token))
            {
                outputExt = ".gdi";
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

        process.ErrorDataReceived += (_, a) =>
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
            if (!process.HasExited)
            {
                process.Kill(true);
                // Wait for process to fully exit and release file handles
                await Task.Run(() => process.WaitForExit(5000), CancellationToken.None);
            }

            // Wait a bit more for file handles to be fully released
            await Task.Delay(500, CancellationToken.None);
            // Clean up partially extracted file
            await TryDeleteFileAsync(outputFile, "partially extracted file", CancellationToken.None);
            throw;
        }
        finally
        {
            ctsSpeed.Cancel();
            await Task.WhenAny(readSpeedTask, Task.Delay(500, CancellationToken.None));
        }

        var success = process.ExitCode == 0 && !token.IsCancellationRequested;

        if (success && deleteOriginal)
        {
            await TryDeleteFileAsync(chdFile, "original CHD file", token);
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
        catch
        {
            return false;
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

            var infoText = output.ToString().ToLowerInvariant();

            // Determine extraction command based on CHD metadata
            if (infoText.Contains("dvd"))
                return "extractdvd";
            if (infoText.Contains("gd-rom"))
                return "extractcd";
            if (infoText.Contains("hard disk") || infoText.Contains("hd") || infoText.Contains("hdd"))
                return "extracthd";

            // Default to CD extraction (most common)
            return "extractcd";
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(true);
            throw;
        }
        catch
        {
            // Default to extractcd if detection fails
            return "extractcd";
        }
    }

    private async Task<bool> ConvertToChdAsync(string chdmanPath, string inputFile, string outputFile, int cores, bool forceCd, bool forceDvd, CancellationToken token)
    {
        using var process = new Process();

        var isImg = inputFile.EndsWith(".img", StringComparison.OrdinalIgnoreCase);
        var isCcd = inputFile.EndsWith(".ccd", StringComparison.OrdinalIgnoreCase);
        var hasCcd = isImg && File.Exists(Path.ChangeExtension(inputFile, ".ccd"));

        var command = forceCd || isCcd || hasCcd || (!forceDvd && !inputFile.EndsWith(".iso", StringComparison.OrdinalIgnoreCase) && !isImg && !inputFile.EndsWith(".raw", StringComparison.OrdinalIgnoreCase))
            ? "createcd"
            : forceDvd || inputFile.EndsWith(".iso", StringComparison.OrdinalIgnoreCase)
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

        process.OutputDataReceived += (_, a) =>
        {
            if (string.IsNullOrEmpty(a.Data)) return;

            // Check for completion messages (these are good news!)
            if (a.Data.Contains("Compression complete") || a.Data.Contains("final ratio"))
            {
                LogMessage($"[CHDMAN ✓] {a.Data}");
            }
            // Filter out progress updates but keep actual errors
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

            // Check for completion messages (these are good news!)
            if (a.Data.Contains("Compression complete") || a.Data.Contains("final ratio"))
            {
                LogMessage($"[CHDMAN ✓] {a.Data}");
            }
            // Filter out progress updates but keep actual errors
            else if (!a.Data.Contains("% complete") &&
                     !a.Data.Contains("Compressing") &&
                     !a.Data.Contains("Output bytes") &&
                     !a.Data.Contains("Compression ratio"))
            {
                LogMessage($"[CHDMAN] {a.Data}");
            }
        };

        using var ctsSpeed = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeoutCts.CancelAfter(TimeSpan.FromHours(AppConfig.MaxConversionTimeoutHours));

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
                    await Task.Delay(AppConfig.WriteSpeedUpdateIntervalMs, speedToken);
                    UpdateWriteSpeedFromPerformanceCounter();
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, speedToken);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
            if (token.IsCancellationRequested && !process.HasExited)
            {
                process.Kill(true);
            }
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(true);
            throw;
        }
        finally
        {
            ctsSpeed.Cancel();
            await Task.WhenAny(speedMonitoringTask, Task.Delay(500, CancellationToken.None));
        }

        return process.ExitCode == 0 && !token.IsCancellationRequested;
    }

    private async Task<PbpExtractionResult> ExtractPbpToCueBinAsync(string psxPackagerPath, string inputFile, string outputFolder, CancellationToken token)
    {
        using var process = new Process();
        var args = $"-i \"{inputFile}\" -o \"{outputFolder}\" -x";
        LogMessage($"PSXPACKAGER: Extracting {Path.GetFileName(inputFile)}");

        process.StartInfo = new ProcessStartInfo
        {
            FileName = psxPackagerPath,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            ErrorDialog = false
        };

        process.OutputDataReceived += (_, a) =>
        {
            if (!string.IsNullOrEmpty(a.Data))
            {
                LogMessage($"[PSXPACKAGER] {a.Data}");
            }
        };

        process.ErrorDataReceived += (_, a) =>
        {
            if (!string.IsNullOrEmpty(a.Data))
            {
                LogMessage($"[PSXPACKAGER ERROR] {a.Data}");
            }
        };

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeoutCts.CancelAfter(TimeSpan.FromHours(AppConfig.MaxConversionTimeoutHours));

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
            if (token.IsCancellationRequested && !process.HasExited)
            {
                process.Kill(true);
                throw new OperationCanceledException();
            }
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(true);
            throw;
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
            if (_writeBytesCounter != null)
            {
                double writeBytesPerSec;
                lock (_performanceCounterLock)
                {
                    writeBytesPerSec = _writeBytesCounter.NextValue();
                }

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
            if (_readBytesCounter != null)
            {
                double readBytesPerSec;
                lock (_performanceCounterLock)
                {
                    readBytesPerSec = _readBytesCounter.NextValue();
                }

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
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(true);
            throw;
        }
        finally
        {
            ctsSpeed.Cancel();
            await Task.WhenAny(readSpeedTask, Task.Delay(500, CancellationToken.None));
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
            var ext = Path.GetExtension(inputFile).ToLowerInvariant();
            switch (ext)
            {
                case ".cue":
                    files.AddRange(await GameFileParser.GetReferencedFilesFromCueAsync(inputFile, LogMessage, token));
                    break;
                case ".gdi":
                    files.AddRange(await GameFileParser.GetReferencedFilesFromGdiAsync(inputFile, LogMessage, token));
                    break;
                case ".toc":
                    files.AddRange(await GameFileParser.GetReferencedFilesFromTocAsync(inputFile, LogMessage, token));
                    break;
                case ".ccd":
                    // CloneCD references: .img, .sub
                    var imgFile = Path.ChangeExtension(inputFile, ".img");
                    var subFile = Path.ChangeExtension(inputFile, ".sub");
                    if (File.Exists(imgFile)) files.Add(imgFile);
                    if (File.Exists(subFile)) files.Add(subFile);
                    break;
            }

            foreach (var f in files.Distinct()) await TryDeleteFileAsync(f, "game file", token);
        }
        catch (Exception ex)
        {
            LogMessage($"Delete error: {ex.Message}");
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
        if (IsLoaded) RefreshFileListForActiveTab();
    }

    private void ForceCreateCdCheckBox_Checked(object sender, RoutedEventArgs e)
    {
        ForceCreateDvdCheckBox?.IsChecked = false;
    }

    private void ForceCreateDvdCheckBox_Checked(object sender, RoutedEventArgs e)
    {
        ForceCreateCdCheckBox?.IsChecked = false;
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
            if (App.SharedBugReportService != null) await App.SharedBugReportService.SendBugReportAsync(msg, ex);
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
        Close();
    }

    private void AboutMenuItem_Click(object sender, RoutedEventArgs e)
    {
        new AboutWindow { Owner = this }.ShowDialog();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _writeBytesCounter?.Dispose();
        _readBytesCounter?.Dispose();
        _archiveService.Dispose();
        _operationTimer.Stop();
        GC.SuppressFinalize(this);
    }
}