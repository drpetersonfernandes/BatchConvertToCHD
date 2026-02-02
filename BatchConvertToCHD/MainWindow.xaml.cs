using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using BatchConvertToCHD.Services;
using BatchConvertToCHD.Utilities;

namespace BatchConvertToCHD;

public partial class MainWindow : IDisposable
{
    private CancellationTokenSource _cts;
    private readonly string _maxCsoPath;
    private readonly bool _isMaxCsoAvailable;
    private readonly bool _isChdmanAvailable;

    private static readonly string[] AllSupportedInputExtensionsForConversion = [".cue", ".iso", ".img", ".cdi", ".gdi", ".toc", ".raw", ".zip", ".7z", ".rar", ".cso"];
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

    // Performance counter for write speed monitoring
    private const int MaxLogLength = 100000; // Maximum characters before log truncation
    private PerformanceCounter? _writeBytesCounter;
    private PerformanceCounter? _readBytesCounter;

    public MainWindow()
    {
        InitializeComponent();
        _cts = new CancellationTokenSource();

        var appDirectory = AppDomain.CurrentDomain.BaseDirectory;
        var chdmanPath = Path.Combine(appDirectory, AppConfig.ChdmanExeName);
        _isChdmanAvailable = File.Exists(chdmanPath);

        _maxCsoPath = Path.Combine(appDirectory, "maxcso.exe");
        _isMaxCsoAvailable = File.Exists(_maxCsoPath) && !AppConfig.IsArm64;

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

            // Defer update check until window is responsive
            await Task.Delay(100); // Allow UI to render first
            _ = _updateService.CheckForNewVersionAsync(LogMessage, UpdateStatusBarMessage, ReportBugAsync);
        }
        catch (Exception ex)
        {
            _ = ReportBugAsync("MainWindow_Loaded error", ex);
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
        if (!App.IsSevenZipAvailable) LogMessage("WARNING: 7z_x64.dll not found.");
        LogMessage("--- Ready for Conversion ---");
    }

    private void DisplayVerificationInstructionsInLog()
    {
        LogMessage($"Welcome to {AppConfig.ApplicationName}. (Verification Mode)");
        if (!_isChdmanAvailable) LogMessage("WARNING: chdman.exe not found!");
        LogMessage("--- Ready for Verification ---");
    }

    private void MainTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source is not TabControl tabControl) return;
        if (!StartConversionButton.IsEnabled || !StartVerificationButton.IsEnabled) return;

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
                case "RewriteTab":
                    LogMessage($"Welcome to {AppConfig.ApplicationName}. (Rewrite Mode)");
                    LogMessage("--- Ready for CHD Rewrite ---");
                    UpdateStatusBarMessage("Ready for CHD rewrite");
                    break;
            }
        }

        UpdateWriteSpeedDisplay(0);
        UpdateReadSpeedDisplay(0);
    }

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        _cts.Cancel();
    }

    private void LogMessage(string message)
    {
        var timestampedMessage = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            LogViewer.AppendText($"{timestampedMessage}{Environment.NewLine}");

            // Truncate log if it gets too large
            if (LogViewer.Text.Length > MaxLogLength)
            {
                var excessLength = LogViewer.Text.Length - MaxLogLength + 1000; // Keep some buffer
                var firstNewline = LogViewer.Text.IndexOf('\n', excessLength);
                if (firstNewline > 0)
                {
                    LogViewer.Text = string.Concat($"[{DateTime.Now:HH:mm:ss.fff}] --- Log truncated due to size ---{Environment.NewLine}", LogViewer.Text.AsSpan(firstNewline + 1));
                }
            }

            LogViewer.ScrollToEnd();
        });
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

    private void HandleFolderBrowse(TextBox targetBox, string logName)
    {
        var folder = SelectFolder($"Select {logName} folder");
        if (string.IsNullOrEmpty(folder)) return;

        var normalized = PathUtils.ValidateAndNormalizePath(folder, logName, ShowError, LogMessage);
        if (normalized != null)
        {
            targetBox.Text = normalized;
        }

        UpdateStatusBarMessage($"{logName} folder selected");
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

            // Safely dispose old CTS before creating a new one to avoid ObjectDisposedException
            var oldCts = _cts;
            _cts = new CancellationTokenSource();
            oldCts.Dispose();

            ResetOperationStats();
            SetControlsState(false);
            await Task.Yield();
            _operationTimer.Restart();
            ResetSpeedCounters();

            var deleteFiles = DeleteOriginalsCheckBox.IsChecked ?? false;
            var smallestFirst = ProcessSmallestFirstCheckBox.IsChecked ?? false;
            var forceCd = ForceCreateCdCheckBox.IsChecked ?? false;
            var forceDvd = ForceCreateDvdCheckBox.IsChecked ?? false;

            LogMessage("--- Starting batch conversion process... ---");

            try
            {
                await PerformBatchConversionAsync(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, AppConfig.ChdmanExeName),
                    inputFolder, outputFolder, deleteFiles, smallestFirst, forceCd, forceDvd, _cts.Token);
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

            // Safely dispose old CTS before creating a new one to avoid ObjectDisposedException
            var oldCts = _cts;
            _cts = new CancellationTokenSource();
            oldCts.Dispose();

            ResetOperationStats();
            SetControlsState(false);
            await Task.Yield();
            _operationTimer.Restart();
            ResetSpeedCounters();

            var includeSub = VerificationIncludeSubfoldersCheckBox.IsChecked ?? false;
            var moveSuccess = MoveSuccessFilesCheckBox.IsChecked ?? false;
            var moveFailed = MoveFailedFilesCheckBox.IsChecked ?? false;
            var successFolder = moveSuccess ? Path.Combine(inputFolder, "Success") : string.Empty;
            var failedFolder = moveFailed ? Path.Combine(inputFolder, "Failed") : string.Empty;

            LogMessage("--- Starting batch verification process... ---");

            try
            {
                await PerformBatchVerificationAsync(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, AppConfig.ChdmanExeName),
                    inputFolder, includeSub, moveSuccess, successFolder, moveFailed, failedFolder, _cts.Token);
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
        DeleteOriginalsCheckBox.IsEnabled = enabled;
        StartConversionButton.IsEnabled = enabled;
        ForceCreateCdCheckBox.IsEnabled = enabled;
        ForceCreateDvdCheckBox.IsEnabled = enabled;
        VerificationInputFolderTextBox.IsEnabled = enabled;
        BrowseVerificationInputButton.IsEnabled = enabled;
        VerificationIncludeSubfoldersCheckBox.IsEnabled = enabled;
        StartVerificationButton.IsEnabled = enabled;
        MoveSuccessFilesCheckBox.IsEnabled = enabled;
        MoveFailedFilesCheckBox.IsEnabled = enabled;
        MainTabControl.IsEnabled = enabled;
        ProgressText.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        ProgressBar.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        ProgressBar.IsIndeterminate = !enabled; // Start moving immediately
        CancelButton.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;

        if (!enabled)
        {
            var tab = MainTabControl.SelectedItem as TabItem;
            UpdateStatusBarMessage(tab?.Name == "ConvertTab" ? "Converting files..." : "Verifying files...");
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

    private async Task PerformBatchConversionAsync(string chdmanPath, string inputFolder, string outputFolder, bool deleteFiles, bool processSmallestFirst, bool forceCd, bool forceDvd, CancellationToken token)
    {
        var filesToConvert = await Task.Run(() =>
        {
            var files = Directory.GetFiles(inputFolder, "*.*", SearchOption.TopDirectoryOnly)
                .Where(static file => AllSupportedInputExtensionsForConversion.Contains(Path.GetExtension(file).ToLowerInvariant()))
                .ToList();
            return processSmallestFirst
                ? files.OrderBy(static f => new FileInfo(f).Length).ToArray()
                : files.ToArray();
        }, token);

        _totalFilesProcessed = filesToConvert.Length;
        UpdateStatsDisplay();
        LogMessage($"Found {_totalFilesProcessed} files to process.");
        if (_totalFilesProcessed == 0) return;

        ProgressBar.Maximum = _totalFilesProcessed;
        var processedCount = 0;
        var cores = Environment.ProcessorCount;
        ResetSpeedCounters();

        foreach (var file in filesToConvert)
        {
            // Update text to show we are starting this file, but bar stays at 'processedCount'
            UpdateProgressDisplay(processedCount, _totalFilesProcessed, Path.GetFileName(file), "Converting");

            var success = await ProcessSingleFileForConversionAsync(chdmanPath, file, outputFolder, deleteFiles, cores, forceCd, forceDvd, token);
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

    private async Task<bool> ProcessSingleFileForConversionAsync(string chdmanPath, string inputFile, string outputFolder, bool deleteOriginal, int cores, bool forceCd, bool forceDvd, CancellationToken token)
    {
        var originalName = Path.GetFileName(inputFile);
        LogMessage($"Processing: {originalName}");
        string fileToProcess;
        var ext = Path.GetExtension(inputFile).ToLowerInvariant();
        var tempDir = string.Empty;
        var outputChd = string.Empty;

        try
        {
            token.ThrowIfCancellationRequested();
            var chdBase = Path.GetFileNameWithoutExtension(originalName);
            outputChd = Path.Combine(outputFolder, PathUtils.SanitizeFileName(chdBase) + ".chd");

            if (ext == ".cso")
            {
                tempDir = Path.Combine(Path.GetTempPath(), $"{TempDirPrefix}{Guid.NewGuid():N}");
                await Task.Run(() => Directory.CreateDirectory(tempDir), token);
                var tempIso = PathUtils.GetSafeTempFileName(originalName, "iso", tempDir);

                var result = await _archiveService.ExtractCsoAsync(inputFile, tempIso, tempDir, LogMessage, UpdateWriteSpeedDisplay, token);
                if (!result.Success) return false;

                fileToProcess = result.FilePath;
            }
            else if (ArchiveExtensions.Contains(ext))
            {
                tempDir = Path.Combine(Path.GetTempPath(), $"{TempDirPrefix}{Guid.NewGuid():N}");
                var result = await _archiveService.ExtractArchiveAsync(inputFile, tempDir, LogMessage, token);
                if (!result.Success) return false;

                // Sanitize extracted file
                var extractedName = Path.GetFileName(result.FilePath);
                fileToProcess = PathUtils.GetSafeTempFileName(extractedName, Path.GetExtension(result.FilePath).TrimStart('.'), tempDir);
                await Task.Run(() => File.Copy(result.FilePath, fileToProcess, true), token);
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
                    tempDir = Path.Combine(Path.GetTempPath(), $"{TempDirPrefix}{Guid.NewGuid():N}");
                    await Task.Run(() => Directory.CreateDirectory(tempDir), token);

                    var tempFile = PathUtils.GetSafeTempFileName(originalName, ext.TrimStart('.'), tempDir);
                    await Task.Run(() => File.Copy(inputFile, tempFile, true), token);

                    fileToProcess = tempFile;
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
                    if (ext is ".cue" or ".gdi")
                    {
                        // Parse and delete dependencies + input file
                        await DeleteOriginalGameFilesAsync(inputFile, token);
                    }
                    else
                    {
                        // Just delete input file
                        await TryDeleteFileAsync(inputFile, "original file", token);
                    }
                }

                return true;
            }
            else
            {
                await TryDeleteFileAsync(outputChd, "failed CHD", CancellationToken.None);
                return false;
            }
        }
        catch (Exception ex)
        {
            LogMessage($"Error processing {originalName}: {ex.Message}");
            if (!string.IsNullOrEmpty(outputChd)) await TryDeleteFileAsync(outputChd, "failed CHD", CancellationToken.None);
            return false;
        }
        finally
        {
            if (!string.IsNullOrEmpty(tempDir) && Directory.Exists(tempDir))
                await TryDeleteDirectoryAsync(tempDir, "temp dir", CancellationToken.None);
        }
    }

    private async Task PerformBatchVerificationAsync(string chdmanPath, string inputFolder, bool includeSub, bool moveSuccess, string successFolder, bool moveFailed, string failedFolder, CancellationToken token)
    {
        var files = await Task.Run(() => Directory.GetFiles(inputFolder, "*.chd", includeSub ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly), token);
        _totalFilesProcessed = files.Length;
        UpdateStatsDisplay();
        LogMessage($"Found {_totalFilesProcessed} CHD files.");
        if (_totalFilesProcessed == 0) return;

        if (moveSuccess) Directory.CreateDirectory(successFolder);
        if (moveFailed) Directory.CreateDirectory(failedFolder);

        ProgressBar.Maximum = _totalFilesProcessed;
        var processed = 0;
        ResetSpeedCounters();

        foreach (var file in files)
        {
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

    private async Task<bool> ConvertToChdAsync(string chdmanPath, string inputFile, string outputFile, int cores, bool forceCd, bool forceDvd, CancellationToken token)
    {
        using var process = new Process();
        var command = forceCd || (!forceDvd && !inputFile.EndsWith(".iso", StringComparison.OrdinalIgnoreCase) && !inputFile.EndsWith(".img", StringComparison.OrdinalIgnoreCase) && !inputFile.EndsWith(".raw", StringComparison.OrdinalIgnoreCase))
            ? "createcd"
            : forceDvd || inputFile.EndsWith(".iso", StringComparison.OrdinalIgnoreCase)
                ? "createdvd"
                : inputFile.EndsWith(".img", StringComparison.OrdinalIgnoreCase)
                    ? "createhd"
                    : "createraw";

        var args = $"{command} -i \"{inputFile}\" -o \"{outputFile}\" -f -np {cores}";
        LogMessage($"CHDMAN: {command} {Path.GetFileName(inputFile)}");

        process.StartInfo = new ProcessStartInfo { FileName = chdmanPath, Arguments = args, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };

        process.ErrorDataReceived += (_, a) =>
        {
            if (!string.IsNullOrEmpty(a.Data) && !a.Data.Contains("% complete")) LogMessage($"[CHDMAN ERR] {a.Data}");
            UpdateConversionProgressFromChdman(a.Data);
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
        }
        finally
        {
            ctsSpeed.Cancel();
            await Task.WhenAny(speedMonitoringTask, Task.Delay(500, token));
        }

        return process.ExitCode == 0 && !token.IsCancellationRequested;
    }

    private void UpdateWriteSpeedFromPerformanceCounter()
    {
        try
        {
            if (_writeBytesCounter != null)
            {
                var writeBytesPerSec = _writeBytesCounter.NextValue();
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
                var readBytesPerSec = _readBytesCounter.NextValue();
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
        if (!await ValidateExecutableAccessAsync(chdmanPath, "chdman.exe")) return false;

        process.StartInfo = new ProcessStartInfo
        {
            FileName = chdmanPath,
            Arguments = $"verify -i \"{chdFile}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(chdmanPath)
        };

        // chdman often sends progress to Standard Error
        process.OutputDataReceived += (_, a) =>
        {
            if (!string.IsNullOrEmpty(a.Data) && a.Data.Contains("% complete"))
            {
                UpdateReadSpeedFromPerformanceCounter();
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
            ctsSpeed.Cancel();
            await Task.WhenAny(readSpeedTask, Task.Delay(500, token));
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
            if (speed > 0 && !StartVerificationButton.IsEnabled)
            {
                StatusBarMessage.Text = "Verifying...";
            }
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

    private static void UpdateConversionProgressFromChdman(string? line)
    {
        if (string.IsNullOrEmpty(line)) return;

        var match = MyRegex().Match(line);
        if (match.Success)
        {
            /* Optional: Update a specific progress bar if needed */
        }
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
        _writeBytesCounter?.NextValue();
        _readBytesCounter?.NextValue();
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
        _operationTimer.Stop();
        GC.SuppressFinalize(this);
    }

    [GeneratedRegex(@"Compressing\s+(?:(?:\d+/\d+)|(?:hunk\s+\d+))\s+\((?<percent>\d+[\.,]?\d*)%\)")]
    private static partial Regex MyRegex();
}