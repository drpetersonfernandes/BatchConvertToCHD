using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows;
using System.Windows.Controls; // Required for TabControl
using Microsoft.Win32;

namespace BatchConvertToCHD;

public partial class MainWindow : IDisposable
{
    private CancellationTokenSource _cts;
    private readonly BugReportService _bugReportService;

    // Bug Report API configuration
    private const string BugReportApiUrl = "https://www.purelogiccode.com/bugreport/api/send-bug-report";
    private const string BugReportApiKey = "hjh7yu6t56tyr540o9u8767676r5674534453235264c75b6t7ggghgg76trf564e";
    private const string ApplicationName = "BatchConvertToCHD";
    private static readonly char[] Separator = [' ', '\t'];

    private const string SevenZipExeName = "7z.exe";
    private const string MaxCsoExeName = "maxcso.exe"; // Added for CSO support
    private readonly string _sevenZipPath;
    private readonly string _maxCsoPath; // Added for CSO support
    private readonly bool _isSevenZipAvailable;
    private readonly bool _isMaxCsoAvailable; // Added for CSO support
    private readonly bool _isChdmanAvailable;

    private static readonly string[] AllSupportedInputExtensionsForConversion = { ".cue", ".iso", ".img", ".cdi", ".gdi", ".toc", ".raw", ".zip", ".7z", ".rar", ".cso" }; // Added .cso
    private static readonly string[] ArchiveExtensions = { ".zip", ".7z", ".rar" }; // CSO is handled separately, not as a generic archive
    private static readonly string[] PrimaryTargetExtensionsInsideArchive = { ".cue", ".iso", ".img", ".cdi", ".gdi", ".toc", ".raw" }; // CSO extracts to .iso

    private int _currentDegreeOfParallelismForFiles = 1;

    // Statistics
    private int _totalFilesProcessed;
    private int _processedOkCount; // Will be displayed as "Success"
    private int _failedCount;
    private readonly Stopwatch _operationTimer = new();

    // For Write Speed Calculation
    private const int WriteSpeedUpdateIntervalMs = 1000; // Update every 1 second


    public MainWindow()
    {
        InitializeComponent();
        _cts = new CancellationTokenSource();

        _bugReportService = new BugReportService(BugReportApiUrl, BugReportApiKey, ApplicationName);

        var appDirectory = AppDomain.CurrentDomain.BaseDirectory;
        var chdmanPath = Path.Combine(appDirectory, "chdman.exe");
        _isChdmanAvailable = File.Exists(chdmanPath);

        _sevenZipPath = Path.Combine(appDirectory, SevenZipExeName);
        _isSevenZipAvailable = File.Exists(_sevenZipPath);

        _maxCsoPath = Path.Combine(appDirectory, MaxCsoExeName); // Added for CSO support
        _isMaxCsoAvailable = File.Exists(_maxCsoPath); // Added for CSO support

        DisplayConversionInstructionsInLog();
        ResetOperationStats();
    }

    private void DisplayConversionInstructionsInLog()
    {
        LogMessage($"Welcome to {ApplicationName}. (Conversion Mode)");
        LogMessage("");
        LogMessage("This program will convert the following formats to CHD:");
        LogMessage("- CUE+BIN files (CD images)");
        LogMessage("- ISO files (CD images)");
        LogMessage("- CDI files (CD images)");
        LogMessage("- GDI files (GD-ROM images)");
        LogMessage("- TOC files (CD images)");
        LogMessage("- IMG files (Hard disk images)");
        LogMessage("- RAW files (Raw data)");
        LogMessage("- CSO files (Compressed ISO, will be decompressed to ISO first)"); // Added for CSO
        LogMessage("- ZIP, 7Z, RAR files (containing any of the above formats)");
        LogMessage("");
        LogMessage("Please follow these steps for conversion:");
        LogMessage("1. Select the input folder containing files to convert");
        LogMessage("2. Select the output folder where CHD files will be saved");
        LogMessage("3. Choose whether to delete original files after conversion");
        LogMessage("4. Optionally, enable parallel processing for faster conversion of multiple files");
        LogMessage("5. Click 'Start Conversion' to begin the process");
        LogMessage("");

        if (_isChdmanAvailable)
        {
            LogMessage("chdman.exe found in the application directory.");
        }
        else
        {
            LogMessage("WARNING: chdman.exe not found in the application directory!");
            LogMessage("Conversion and Verification will not function without chdman.exe.");
            Task.Run(async () => await ReportBugAsync("chdman.exe not found on startup. This will prevent the application from functioning correctly."));
        }

        if (_isSevenZipAvailable) LogMessage("7z.exe found. .7z and .rar extraction enabled for conversion.");
        else LogMessage("WARNING: 7z.exe not found. .7z and .rar extraction will be disabled for conversion.");

        if (_isMaxCsoAvailable) LogMessage("maxcso.exe found. .cso decompression enabled for conversion."); // Added for CSO
        else LogMessage("WARNING: maxcso.exe not found. .cso decompression will be disabled for conversion."); // Added for CSO

        LogMessage("--- Ready for Conversion ---");
    }

    private void DisplayVerificationInstructionsInLog()
    {
        LogMessage($"Welcome to {ApplicationName}. (Verification Mode)");
        LogMessage("");
        LogMessage("This program will verify the integrity of all CHD files in the selected folder.");
        LogMessage("It will check each file's structure and validate its data against internal checksums.");
        LogMessage("");
        LogMessage("Please follow these steps for verification:");
        LogMessage("1. Select the folder containing CHD files to verify");
        LogMessage("2. Choose whether to include subfolders in the search");
        LogMessage("3. Optionally, choose to move successfully tested files to a 'Success Folder'");
        LogMessage("4. Optionally, choose to move failed tested files to a 'Failed Folder'");
        LogMessage("5. Click 'Start Verification' to begin the process");
        LogMessage("");

        if (_isChdmanAvailable)
        {
            LogMessage("chdman.exe found in the application directory.");
        }
        else
        {
            LogMessage("WARNING: chdman.exe not found in the application directory!");
            LogMessage("Verification will not function without chdman.exe.");
        }

        LogMessage("--- Ready for Verification ---");
    }


    private void MainTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source is not TabControl tabControl) return;

        if (!StartConversionButton.IsEnabled || !StartVerificationButton.IsEnabled) return;

        Application.Current.Dispatcher.Invoke(() => LogViewer.Clear());
        if (tabControl.SelectedItem is TabItem selectedTab)
        {
            switch (selectedTab.Name)
            {
                case "ConvertTab":
                    DisplayConversionInstructionsInLog();
                    break;
                case "VerifyTab":
                    DisplayVerificationInstructionsInLog();
                    break;
            }
        }

        // Reset write speed display when switching tabs and not busy
        UpdateWriteSpeedDisplay(0);
    }

    private void MoveSuccessFilesCheckBox_CheckedUnchecked(object sender, RoutedEventArgs e)
    {
        SuccessFolderPanel.Visibility = MoveSuccessFilesCheckBox.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SetControlsState(StartConversionButton.IsEnabled); // Re-evaluate dependent control states
    }

    private void MoveFailedFilesCheckBox_CheckedUnchecked(object sender, RoutedEventArgs e)
    {
        FailedFolderPanel.Visibility = MoveFailedFilesCheckBox.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SetControlsState(StartConversionButton.IsEnabled); // Re-evaluate dependent control states
    }

    private void BrowseSuccessFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var folder = SelectFolder("Select folder for successfully verified CHD files");
        if (!string.IsNullOrEmpty(folder))
        {
            SuccessFolderTextBox.Text = folder;
        }
    }

    private void BrowseFailedFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var folder = SelectFolder("Select folder for failed CHD files");
        if (!string.IsNullOrEmpty(folder))
        {
            FailedFolderTextBox.Text = folder;
        }
    }


    private void Window_Closing(object sender, CancelEventArgs e)
    {
        _cts.Cancel();
        Application.Current.Shutdown();
        Environment.Exit(0);
    }

    private void LogMessage(string message)
    {
        var timestampedMessage = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
        Application.Current.Dispatcher.Invoke(() =>
        {
            LogViewer.AppendText($"{timestampedMessage}{Environment.NewLine}");
            LogViewer.ScrollToEnd();
        });
    }

    private void BrowseConversionInputButton_Click(object sender, RoutedEventArgs e)
    {
        var folder = SelectFolder("Select the folder containing files to convert");
        if (string.IsNullOrEmpty(folder)) return;

        ConversionInputFolderTextBox.Text = folder;
        LogMessage($"Conversion input folder selected: {folder}");
    }

    private void BrowseConversionOutputButton_Click(object sender, RoutedEventArgs e)
    {
        var folder = SelectFolder("Select the output folder for CHD files");
        if (string.IsNullOrEmpty(folder)) return;

        ConversionOutputFolderTextBox.Text = folder;
        LogMessage($"Conversion output folder selected: {folder}");
    }

    private void BrowseVerificationInputButton_Click(object sender, RoutedEventArgs e)
    {
        var folder = SelectFolder("Select the folder containing CHD files to verify");
        if (string.IsNullOrEmpty(folder)) return;

        VerificationInputFolderTextBox.Text = folder;
        LogMessage($"Verification input folder selected: {folder}");
    }

    private async void StartConversionButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!_isChdmanAvailable)
            {
                LogMessage("Error: chdman.exe not found. Cannot start conversion.");
                ShowError("chdman.exe is missing. Please ensure it's in the application directory.");
                return;
            }

            var inputFolder = ConversionInputFolderTextBox.Text;
            var outputFolder = ConversionOutputFolderTextBox.Text;
            var deleteFiles = DeleteOriginalsCheckBox.IsChecked ?? false;
            var useParallelFileProcessing = ParallelProcessingCheckBox.IsChecked ?? false;

            _currentDegreeOfParallelismForFiles = useParallelFileProcessing ? 3 : 1;

            if (string.IsNullOrEmpty(inputFolder) || string.IsNullOrEmpty(outputFolder))
            {
                LogMessage("Error: Input or output folder not selected for conversion.");
                ShowError("Please select both input and output folders for conversion.");
                return;
            }

            if (inputFolder.Equals(outputFolder, StringComparison.OrdinalIgnoreCase))
            {
                LogMessage("Error: Input and output folders cannot be the same for conversion.");
                ShowError("Input and output folders must be different for conversion.");
                return;
            }

            if (_cts.IsCancellationRequested)
            {
                _cts.Dispose();
                _cts = new CancellationTokenSource();
            }
            else // If not previously cancelled, ensure it's a new one for this run
            {
                _cts = new CancellationTokenSource();
            }


            ResetOperationStats();
            SetControlsState(false);
            _operationTimer.Restart();

            LogMessage("--- Starting batch conversion process... ---");
            LogMessage($"Input folder: {inputFolder}");
            LogMessage($"Output folder: {outputFolder}");
            LogMessage($"Delete original files: {deleteFiles}");
            LogMessage($"Parallel file processing: {useParallelFileProcessing} (Max concurrency: {_currentDegreeOfParallelismForFiles})");

            try
            {
                await PerformBatchConversionAsync(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "chdman.exe"), inputFolder, outputFolder, deleteFiles, useParallelFileProcessing, _currentDegreeOfParallelismForFiles);
            }
            catch (OperationCanceledException)
            {
                LogMessage("Conversion operation was canceled by user.");
            }
            catch (Exception ex)
            {
                LogMessage($"Error during batch conversion: {ex.Message}");
                await ReportBugAsync("Error during batch conversion process", ex);
            }
            finally
            {
                _operationTimer.Stop();
                UpdateProcessingTimeDisplay();
                UpdateWriteSpeedDisplay(0); // Reset speed after batch
                SetControlsState(true);
                LogOperationSummary("Conversion");
            }
        }
        catch (Exception ex)
        {
            _ = ReportBugAsync("Error during batch conversion process", ex);
        }
    }

    private async void StartVerificationButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!_isChdmanAvailable)
            {
                LogMessage("Error: chdman.exe not found. Cannot start verification.");
                ShowError("chdman.exe is missing. Please ensure it's in the application directory.");
                return;
            }

            var inputFolder = VerificationInputFolderTextBox.Text;
            var includeSubfolders = VerificationIncludeSubfoldersCheckBox.IsChecked ?? false;
            var moveSuccess = MoveSuccessFilesCheckBox.IsChecked == true;
            var successFolder = SuccessFolderTextBox.Text;
            var moveFailed = MoveFailedFilesCheckBox.IsChecked == true;
            var failedFolder = FailedFolderTextBox.Text;

            if (string.IsNullOrEmpty(inputFolder))
            {
                LogMessage("Error: No input folder selected for verification.");
                ShowError("Please select the input folder containing CHD files to verify.");
                return;
            }

            if (moveSuccess && string.IsNullOrEmpty(successFolder))
            {
                LogMessage("Error: 'Move successfully tested files' is checked, but no Success Folder is selected.");
                ShowError("Please select a Success Folder or uncheck the option to move successful files.");
                return;
            }

            if (moveFailed && string.IsNullOrEmpty(failedFolder))
            {
                LogMessage("Error: 'Move failed tested files' is checked, but no Failed Folder is selected.");
                ShowError("Please select a Failed Folder or uncheck the option to move failed files.");
                return;
            }

            if (moveSuccess && moveFailed && !string.IsNullOrEmpty(successFolder) && successFolder.Equals(failedFolder, StringComparison.OrdinalIgnoreCase))
            {
                LogMessage("Error: Success Folder and Failed Folder cannot be the same.");
                ShowError("Please select different folders for successful and failed files.");
                return;
            }

            if ((moveSuccess && !string.IsNullOrEmpty(successFolder) && successFolder.Equals(inputFolder, StringComparison.OrdinalIgnoreCase)) ||
                (moveFailed && !string.IsNullOrEmpty(failedFolder) && failedFolder.Equals(inputFolder, StringComparison.OrdinalIgnoreCase)))
            {
                LogMessage("Error: Success/Failed folder cannot be the same as the Input folder for verification.");
                ShowError("Please select Success/Failed folders that are different from the Input folder.");
                return;
            }


            if (_cts.IsCancellationRequested)
            {
                _cts.Dispose();
                _cts = new CancellationTokenSource();
            }
            else
            {
                _cts = new CancellationTokenSource();
            }

            ResetOperationStats();
            SetControlsState(false);
            _operationTimer.Restart();

            LogMessage("--- Starting batch verification process... ---");
            LogMessage($"Input folder: {inputFolder}");
            LogMessage($"Include subfolders: {includeSubfolders}");
            if (moveSuccess) LogMessage($"Moving successful files to: {successFolder}");
            if (moveFailed) LogMessage($"Moving failed files to: {failedFolder}");

            try
            {
                await PerformBatchVerificationAsync(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "chdman.exe"), inputFolder, includeSubfolders, moveSuccess, successFolder, moveFailed, failedFolder);
            }
            catch (OperationCanceledException)
            {
                LogMessage("Verification operation was canceled by user.");
            }
            catch (Exception ex)
            {
                LogMessage($"Error during batch verification: {ex.Message}");
                await ReportBugAsync("Error during batch verification process", ex);
            }
            finally
            {
                _operationTimer.Stop();
                UpdateProcessingTimeDisplay();
                UpdateWriteSpeedDisplay(0); // Verification doesn't use write speed
                SetControlsState(true);
                LogOperationSummary("Verification");
            }
        }
        catch (Exception ex)
        {
            _ = ReportBugAsync("Error during batch verification process", ex);
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _cts.Cancel();
        LogMessage("Cancellation requested. Waiting for current operation(s) to complete...");
    }

    private void SetControlsState(bool enabled)
    {
        ConversionInputFolderTextBox.IsEnabled = enabled;
        BrowseConversionInputButton.IsEnabled = enabled;
        ConversionOutputFolderTextBox.IsEnabled = enabled;
        BrowseConversionOutputButton.IsEnabled = enabled;
        DeleteOriginalsCheckBox.IsEnabled = enabled;
        ParallelProcessingCheckBox.IsEnabled = enabled;
        StartConversionButton.IsEnabled = enabled;

        VerificationInputFolderTextBox.IsEnabled = enabled;
        BrowseVerificationInputButton.IsEnabled = enabled;
        VerificationIncludeSubfoldersCheckBox.IsEnabled = enabled;
        StartVerificationButton.IsEnabled = enabled;
        MoveSuccessFilesCheckBox.IsEnabled = enabled;
        SuccessFolderTextBox.IsEnabled = enabled && (MoveSuccessFilesCheckBox.IsChecked == true);
        BrowseSuccessFolderButton.IsEnabled = enabled && (MoveSuccessFilesCheckBox.IsChecked == true);
        MoveFailedFilesCheckBox.IsEnabled = enabled;
        FailedFolderTextBox.IsEnabled = enabled && (MoveFailedFilesCheckBox.IsChecked == true);
        BrowseFailedFolderButton.IsEnabled = enabled && (MoveFailedFilesCheckBox.IsChecked == true);

        SuccessFolderPanel.IsEnabled = enabled;
        FailedFolderPanel.IsEnabled = enabled;

        MainTabControl.IsEnabled = enabled;

        ProgressText.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        ProgressBar.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        CancelButton.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;

        if (!enabled) return;

        ClearProgressDisplay();
        if (MainTabControl.SelectedItem is TabItem selectedTab)
        {
            Application.Current.Dispatcher.Invoke(() => LogViewer.Clear());
            if (selectedTab.Name == "ConvertTab") DisplayConversionInstructionsInLog();
            else if (selectedTab.Name == "VerifyTab") DisplayVerificationInstructionsInLog();
        }

        UpdateWriteSpeedDisplay(0); // Reset speed when controls are re-enabled
    }

    private static string? SelectFolder(string description)
    {
        var dialog = new OpenFolderDialog { Title = description };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    private async Task PerformBatchConversionAsync(string chdmanPath, string inputFolder, string outputFolder, bool deleteFiles, bool useParallelFileProcessing, int maxConcurrency)
    {
        var filesToConvert = Directory.GetFiles(inputFolder, "*.*", SearchOption.TopDirectoryOnly)
            .Where(file => AllSupportedInputExtensionsForConversion.Contains(Path.GetExtension(file).ToLowerInvariant()))
            .ToArray();

        _totalFilesProcessed = filesToConvert.Length;
        UpdateStatsDisplay();
        LogMessage($"Found {_totalFilesProcessed} files to process for conversion.");
        if (_totalFilesProcessed == 0)
        {
            LogMessage("No supported files found in the input folder for conversion.");
            return;
        }

        ProgressBar.Maximum = _totalFilesProcessed;
        var filesActuallyProcessedCount = 0;

        var coresPerConversion = useParallelFileProcessing ? Math.Max(1, Environment.ProcessorCount / 3) : Environment.ProcessorCount;

        if (useParallelFileProcessing && _totalFilesProcessed > 1)
        {
            var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = maxConcurrency, CancellationToken = _cts.Token };
            await Parallel.ForEachAsync(filesToConvert, parallelOptions, async (currentFile, token) =>
            {
                var success = await ProcessSingleFileForConversionAsync(chdmanPath, currentFile, outputFolder, deleteFiles, coresPerConversion, token);
                if (success) Interlocked.Increment(ref _processedOkCount);
                else Interlocked.Increment(ref _failedCount);

                var processedSoFar = Interlocked.Increment(ref filesActuallyProcessedCount);
                UpdateProgressDisplay(processedSoFar, _totalFilesProcessed, Path.GetFileName(currentFile), "Converting");
                UpdateStatsDisplay();
                UpdateProcessingTimeDisplay();
            });
        }
        else
        {
            foreach (var currentFile in filesToConvert)
            {
                if (_cts.Token.IsCancellationRequested) break;

                var success = await ProcessSingleFileForConversionAsync(chdmanPath, currentFile, outputFolder, deleteFiles, coresPerConversion, _cts.Token);
                if (success)
                {
                    _processedOkCount++;
                }
                else
                {
                    _failedCount++;
                }

                filesActuallyProcessedCount++;
                UpdateProgressDisplay(filesActuallyProcessedCount, _totalFilesProcessed, Path.GetFileName(currentFile), "Converting");
                UpdateStatsDisplay();
                UpdateProcessingTimeDisplay();
            }
        }

        UpdateWriteSpeedDisplay(0); // Ensure speed is reset after all conversions
    }

    private async Task<bool> ProcessSingleFileForConversionAsync(string chdmanPath, string inputFile, string outputFolder, bool deleteOriginal, int coresForChdman, CancellationToken token)
    {
        var fileName = Path.GetFileName(inputFile);
        LogMessage($"Starting to process for conversion: {fileName}");
        try
        {
            var fileToProcess = inputFile;
            var isArchiveOrCsoFile = false;
            var tempDir = string.Empty;
            var fileExtension = Path.GetExtension(inputFile).ToLowerInvariant();

            if (fileExtension == ".cso")
            {
                if (!_isMaxCsoAvailable)
                {
                    LogMessage($"Skipping {fileName}: {MaxCsoExeName} is not available for .cso decompression. This file will be marked as failed.");
                    return false;
                }

                LogMessage($"CSO file detected: {fileName}. Attempting decompression.");
                var extractResult = await ExtractCsoAsync(inputFile, token);
                if (extractResult.Success)
                {
                    fileToProcess = extractResult.FilePath; // Path to the extracted .iso
                    tempDir = extractResult.TempDir;
                    isArchiveOrCsoFile = true;
                    LogMessage($"Using decompressed ISO: {Path.GetFileName(fileToProcess)} from CSO {fileName}");
                }
                else
                {
                    LogMessage($"Error decompressing CSO {fileName}: {extractResult.ErrorMessage}");
                    return false;
                }
            }
            else if (ArchiveExtensions.Contains(fileExtension)) // Handles .zip, .7z, .rar
            {
                LogMessage($"Archive detected: {fileName}. Attempting extraction.");
                var extractResult = await ExtractArchiveAsync(inputFile, token);
                if (extractResult.Success)
                {
                    fileToProcess = extractResult.FilePath;
                    tempDir = extractResult.TempDir;
                    isArchiveOrCsoFile = true;
                    LogMessage($"Using extracted file: {Path.GetFileName(fileToProcess)} from archive {fileName}");
                }
                else
                {
                    LogMessage($"Error extracting archive {fileName}: {extractResult.ErrorMessage}");
                    return false;
                }
            }

            try
            {
                var outputChdFile = Path.Combine(outputFolder, Path.GetFileNameWithoutExtension(fileToProcess) + ".chd");
                Directory.CreateDirectory(Path.GetDirectoryName(outputChdFile) ?? outputFolder);

                var success = await ConvertToChdAsync(chdmanPath, fileToProcess, outputChdFile, coresForChdman, token);

                if (success) LogMessage($"Successfully converted: {Path.GetFileName(fileToProcess)} (from {fileName}) to {Path.GetFileName(outputChdFile)}");
                else LogMessage($"Failed to convert: {Path.GetFileName(fileToProcess)} (from {fileName})");

                if (!success || !deleteOriginal) return success;

                if (isArchiveOrCsoFile)
                {
                    TryDeleteFile(inputFile, $"original {fileExtension} file: {fileName}");
                }
                else
                {
                    await DeleteOriginalGameFilesAsync(fileToProcess); // fileToProcess is inputFile in this case
                }

                return success;
            }
            finally
            {
                if (isArchiveOrCsoFile && !string.IsNullOrEmpty(tempDir) && Directory.Exists(tempDir))
                {
                    TryDeleteDirectory(tempDir, "temporary extraction/decompression directory");
                }
            }
        }
        catch (OperationCanceledException)
        {
            LogMessage($"Conversion processing cancelled for {fileName}.");
            throw;
        }
        catch (Exception ex)
        {
            LogMessage($"Error processing file {fileName} for conversion: {ex.Message}");
            await ReportBugAsync($"Error processing file for conversion: {fileName}", ex);
            return false;
        }
    }


    private async Task PerformBatchVerificationAsync(string chdmanPath, string inputFolder, bool includeSubfolders, bool moveSuccess, string successFolder, bool moveFailed, string failedFolder)
    {
        var searchOption = includeSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var filesToVerify = Directory.GetFiles(inputFolder, "*.chd", searchOption);

        _totalFilesProcessed = filesToVerify.Length;
        UpdateStatsDisplay();
        LogMessage($"Found {_totalFilesProcessed} CHD files to verify.");
        if (_totalFilesProcessed == 0)
        {
            LogMessage("No CHD files found in the specified folder for verification.");
            return;
        }

        ProgressBar.Maximum = _totalFilesProcessed;
        var filesActuallyProcessedCount = 0;

        foreach (var chdFile in filesToVerify)
        {
            if (_cts.Token.IsCancellationRequested) break;

            var fileName = Path.GetFileName(chdFile);
            UpdateProgressDisplay(filesActuallyProcessedCount + 1, _totalFilesProcessed, fileName, "Verifying");

            var isValid = await VerifyChdAsync(chdmanPath, chdFile, _cts.Token);

            if (isValid)
            {
                LogMessage($"✓ Verification successful: {fileName}");
                _processedOkCount++;
                if (moveSuccess && !string.IsNullOrEmpty(successFolder))
                {
                    await MoveVerifiedFileAsync(chdFile, successFolder, inputFolder, includeSubfolders, "successfully verified", _cts.Token);
                }
            }
            else
            {
                LogMessage($"✗ Verification failed: {fileName}");
                _failedCount++;
                if (moveFailed && !string.IsNullOrEmpty(failedFolder))
                {
                    await MoveVerifiedFileAsync(chdFile, failedFolder, inputFolder, includeSubfolders, "failed verification", _cts.Token);
                }
            }

            filesActuallyProcessedCount++;
            UpdateStatsDisplay();
            UpdateProcessingTimeDisplay();
        }
    }

    private async Task MoveVerifiedFileAsync(string sourceFile, string destinationParentFolder, string baseInputFolder,
        bool maintainSubfolders, string moveReason, CancellationToken token)
    {
        var fileName = Path.GetFileName(sourceFile);
        string destinationFile;

        try
        {
            if (maintainSubfolders && !string.IsNullOrEmpty(Path.GetDirectoryName(sourceFile)) && Path.GetDirectoryName(sourceFile) != baseInputFolder)
            {
                var relativeDir = Path.GetRelativePath(baseInputFolder, Path.GetDirectoryName(sourceFile) ?? string.Empty);
                var targetDir = Path.Combine(destinationParentFolder, relativeDir);
                destinationFile = Path.Combine(targetDir, fileName);
                Directory.CreateDirectory(targetDir);
            }
            else
            {
                destinationFile = Path.Combine(destinationParentFolder, fileName);
                Directory.CreateDirectory(destinationParentFolder);
            }

            if (File.Exists(destinationFile))
            {
                LogMessage($"  Cannot move {fileName}: Destination file already exists at {destinationFile}. Skipping move.");
                return;
            }

            await Task.Run(() => File.Move(sourceFile, destinationFile), token);
            LogMessage($"  Moved {fileName} ({moveReason}) to {destinationFile}");
            return;
        }
        catch (OperationCanceledException)
        {
            LogMessage($"  Move operation for {fileName} cancelled.");
            throw;
        }
        catch (Exception ex)
        {
            LogMessage($"  Error moving {fileName} to {destinationParentFolder}: {ex.Message}");
            await ReportBugAsync($"Error moving verified file {fileName}", ex);
            return;
        }
    }


    private async Task<bool> ConvertToChdAsync(string chdmanPath, string inputFile, string outputFile, int coresForChdman, CancellationToken token)
    {
        var process = new Process();
        try
        {
            var command = "createcd";
            var extension = Path.GetExtension(inputFile).ToLowerInvariant();
            switch (extension)
            {
                case ".img": command = "createhd"; break;
                case ".raw": command = "createraw"; break;
                // .iso, .cue, .gdi, .cdi, .toc will use createcd by default
            }

            coresForChdman = Math.Max(1, coresForChdman);

            LogMessage($"CHDMAN Convert: Using command '{command}' with {coresForChdman} core(s) for {Path.GetFileName(inputFile)}.");
            var arguments = $"{command} -i \"{inputFile}\" -o \"{outputFile}\" -f -np {coresForChdman}";

            process.StartInfo = new ProcessStartInfo
            {
                FileName = chdmanPath,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            process.EnableRaisingEvents = true;

            var outputBuilder = new StringBuilder();
            var errorBuilder = new StringBuilder();
            process.OutputDataReceived += (_, args) =>
            {
                if (!string.IsNullOrEmpty(args.Data)) outputBuilder.AppendLine(args.Data);
                UpdateConversionProgressFromChdman(args.Data);
            };
            process.ErrorDataReceived += (_, args) =>
            {
                if (string.IsNullOrEmpty(args.Data)) return;

                errorBuilder.AppendLine(args.Data);
                LogMessage($"[CHDMAN CONVERT STDERR] {args.Data}");
                UpdateConversionProgressFromChdman(args.Data);
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // Write speed monitoring
            var lastSpeedCheckTime = DateTime.UtcNow;
            long lastFileSize = 0;
            try
            {
                var outputFileInfo = new FileInfo(outputFile);
                if (outputFileInfo.Exists)
                {
                    lastFileSize = outputFileInfo.Length;
                }
            }
            catch
            {
                /* ignore if file doesn't exist yet */
            }


            while (!process.HasExited)
            {
                if (token.IsCancellationRequested)
                {
                    try
                    {
                        if (!process.HasExited) process.Kill(true);
                    }
                    catch
                    {
                        /* ignore */
                    }

                    LogMessage($"Conversion process for {Path.GetFileName(inputFile)} cancelled by user.");
                    UpdateWriteSpeedDisplay(0);
                    return false; // Or throw OperationCanceledException
                }

                await Task.Delay(WriteSpeedUpdateIntervalMs, token).ContinueWith(tsk => { }, token); // ContinueWith to avoid throwing on cancel from delay itself

                if (process.HasExited || token.IsCancellationRequested) break;

                try
                {
                    var currentFileInfo = new FileInfo(outputFile);
                    currentFileInfo.Refresh(); // Get latest info
                    if (currentFileInfo.Exists)
                    {
                        var currentFileSize = currentFileInfo.Length;
                        var currentTime = DateTime.UtcNow;
                        var timeDelta = currentTime - lastSpeedCheckTime;

                        if (timeDelta.TotalSeconds > 0)
                        {
                            var bytesDelta = currentFileSize - lastFileSize;
                            var speed = (bytesDelta / timeDelta.TotalSeconds) / (1024.0 * 1024.0); // MB/s
                            UpdateWriteSpeedDisplay(speed);
                        }

                        lastFileSize = currentFileSize;
                        lastSpeedCheckTime = currentTime;
                    }
                }
                catch (FileNotFoundException)
                {
                    /* File might not be created yet, or deleted */
                }
                catch (Exception ex)
                {
                    LogMessage($"Write speed monitoring error: {ex.Message}");
                }
            }

            await process.WaitForExitAsync(token).ContinueWith(tsk => { }, token); // Wait for final exit,
                                                                                   // ignore OCE from here
            UpdateWriteSpeedDisplay(0); // Reset speed after this file
            return process.ExitCode == 0;
        }
        catch (OperationCanceledException)
        {
            LogMessage($"Conversion cancelled for {Path.GetFileName(inputFile)}.");
            try
            {
                if (!process.HasExited) process.Kill(true);
            }
            catch
            {
                /* ignore */
            }

            UpdateWriteSpeedDisplay(0);
            throw;
        }
        catch (Exception ex)
        {
            LogMessage($"Error converting file {Path.GetFileName(inputFile)}: {ex.Message}");
            await ReportBugAsync($"Error converting file: {Path.GetFileName(inputFile)} with chdman", ex);
            UpdateWriteSpeedDisplay(0);
            return false;
        }
        finally
        {
            process?.Dispose();
        }
    }

    private async Task<bool> VerifyChdAsync(string chdmanPath, string chdFile, CancellationToken token)
    {
        var process = new Process();
        try
        {
            process.StartInfo = new ProcessStartInfo
            {
                FileName = chdmanPath,
                Arguments = $"verify -i \"{chdFile}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            process.EnableRaisingEvents = true;

            process.OutputDataReceived += (s, args) =>
            {
                if (!string.IsNullOrEmpty(args.Data)) LogMessage($"[CHDMAN VERIFY STDOUT] {args.Data}");
            };
            process.ErrorDataReceived += (s, args) =>
            {
                if (string.IsNullOrEmpty(args.Data)) return;

                if (args.Data.Contains("Verifying,") && args.Data.Contains("% complete")) LogMessage($"CHDMAN Verify: {args.Data}");
                else LogMessage($"[CHDMAN VERIFY STDERR] {args.Data}");
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync(token);
            return process.ExitCode == 0;
        }
        catch (OperationCanceledException)
        {
            LogMessage($"Verification cancelled for {Path.GetFileName(chdFile)}.");
            try
            {
                if (!process.HasExited) process.Kill(true);
            }
            catch
            {
                /* ignore */
            }

            throw;
        }
        catch (Exception ex)
        {
            LogMessage($"Error verifying file {Path.GetFileName(chdFile)}: {ex.Message}");
            await ReportBugAsync($"Error verifying file: {Path.GetFileName(chdFile)} with chdman", ex);
            return false;
        }
        finally
        {
            process?.Dispose();
        }
    }

    private void ResetOperationStats()
    {
        _totalFilesProcessed = 0;
        _processedOkCount = 0;
        _failedCount = 0;
        _operationTimer.Reset();
        UpdateStatsDisplay();
        UpdateProcessingTimeDisplay();
        UpdateWriteSpeedDisplay(0); // Reset write speed display
        ClearProgressDisplay();
    }

    private void UpdateStatsDisplay()
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            TotalFilesValue.Text = _totalFilesProcessed.ToString(CultureInfo.InvariantCulture);
            SuccessValue.Text = _processedOkCount.ToString(CultureInfo.InvariantCulture); // Changed from ProcessedOkValue
            FailedValue.Text = _failedCount.ToString(CultureInfo.InvariantCulture);
        });
    }

    private void UpdateProcessingTimeDisplay()
    {
        var elapsed = _operationTimer.Elapsed;
        Application.Current.Dispatcher.Invoke(() =>
        {
            ProcessingTimeValue.Text = $@"{elapsed:hh\:mm\:ss}";
        });
    }

    private void UpdateWriteSpeedDisplay(double speedInMBps)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            WriteSpeedValue.Text = $"{speedInMBps:F1} MB/s";
        });
    }

    private void UpdateProgressDisplay(int current, int total, string currentFileName, string operationVerb)
    {
        var percentage = total == 0 ? 0 : (double)current / total * 100;
        Application.Current.Dispatcher.Invoke(() =>
        {
            ProgressText.Text = $"{operationVerb} file {current} of {total}: {currentFileName} ({percentage:F1}%)";
            ProgressBar.Value = current;
            ProgressBar.Maximum = total > 0 ? total : 1;
            ProgressText.Visibility = Visibility.Visible;
            ProgressBar.Visibility = Visibility.Visible;
        });
    }

    private void ClearProgressDisplay()
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            ProgressBar.Value = 0;
            ProgressBar.Visibility = Visibility.Collapsed;
            ProgressText.Text = string.Empty;
            ProgressText.Visibility = Visibility.Collapsed;
        });
    }

    private void UpdateConversionProgressFromChdman(string? progressLine)
    {
        if (string.IsNullOrEmpty(progressLine)) return;

        try
        {
            var match = ChdmanCompressionProgressRegex().Match(progressLine);
            if (!match.Success) return;

            var percentageStr = match.Groups["percent"].Value.Replace(',', '.');
            if (!double.TryParse(percentageStr, NumberStyles.Any, CultureInfo.InvariantCulture, out _)) return;

            var ratioMatch = ChdmanCompressionRatioRegex().Match(progressLine);
            if (ratioMatch.Success)
            {
            }
        }
        catch (Exception ex)
        {
            LogMessage($"Error updating chdman conversion progress detail: {ex.Message}");
        }
    }

    private List<string> GetReferencedFilesFromCue(string cuePath)
    {
        var referencedFiles = new List<string>();
        var cueDir = Path.GetDirectoryName(cuePath) ?? string.Empty;
        try
        {
            var lines = File.ReadAllLines(cuePath);
            foreach (var line in lines)
            {
                var trimmedLine = line.Trim();
                if (!trimmedLine.StartsWith("FILE ", StringComparison.OrdinalIgnoreCase)) continue;

                var parts = trimmedLine.Split('"');
                if (parts.Length < 2) continue;

                var fileName = parts[1];
                referencedFiles.Add(Path.Combine(cueDir, fileName));
            }
        }
        catch (Exception ex)
        {
            LogMessage($"Error parsing CUE file {Path.GetFileName(cuePath)}: {ex.Message}");
            Task.Run(async () => await ReportBugAsync($"Error parsing CUE file: {Path.GetFileName(cuePath)}", ex));
        }

        return referencedFiles;
    }

    private List<string> GetReferencedFilesFromGdi(string gdiPath)
    {
        var referencedFiles = new List<string>();
        var gdiDir = Path.GetDirectoryName(gdiPath) ?? string.Empty;
        try
        {
            var lines = File.ReadAllLines(gdiPath);
            for (var i = 1; i < lines.Length; i++)
            {
                var trimmedLine = lines[i].Trim();
                if (string.IsNullOrWhiteSpace(trimmedLine)) continue;

                var parts = trimmedLine.Split(Separator, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 5) continue;

                referencedFiles.Add(Path.Combine(gdiDir, parts[4].Trim('"')));
            }
        }
        catch (Exception ex)
        {
            LogMessage($"Error parsing GDI file {Path.GetFileName(gdiPath)}: {ex.Message}");
            Task.Run(async () => await ReportBugAsync($"Error parsing GDI file: {Path.GetFileName(gdiPath)}", ex));
        }

        return referencedFiles;
    }

    private async Task<(bool Success, string FilePath, string TempDir, string ErrorMessage)> ExtractCsoAsync(string csoPath, CancellationToken token)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var csoFileName = Path.GetFileName(csoPath);
        var outputIsoName = Path.ChangeExtension(csoFileName, ".iso");
        var outputIsoPath = Path.Combine(tempDir, outputIsoName);
        var process = new Process();

        try
        {
            // _isMaxCsoAvailable check is now done in ProcessSingleFileForConversionAsync before calling this
            Directory.CreateDirectory(tempDir);
            LogMessage($"Decompressing {csoFileName} to temporary ISO: {outputIsoPath}");

            process.StartInfo = new ProcessStartInfo
            {
                FileName = _maxCsoPath,
                Arguments = $"decompress \"{csoPath}\" \"{outputIsoPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var outputBuilder = new StringBuilder(); // To capture output for error reporting
            process.OutputDataReceived += (_, args) =>
            {
                if (args.Data != null) outputBuilder.AppendLine(args.Data);
            };
            process.ErrorDataReceived += (_, args) =>
            {
                if (args.Data == null) return;

                outputBuilder.AppendLine(args.Data);
                LogMessage($"[MAXCSO STDERR] {args.Data}"); // Log errors immediately
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync(token);

            if (process.ExitCode != 0)
            {
                LogMessage($"Error decompressing {csoFileName} with {MaxCsoExeName}. Exit code: {process.ExitCode}. Output: {outputBuilder.ToString().Trim()}");
                return (false, string.Empty, tempDir, $"{MaxCsoExeName} failed. Exit code: {process.ExitCode}. Output: {outputBuilder.ToString().Trim()}");
            }

            if (!File.Exists(outputIsoPath))
            {
                LogMessage($"Decompression of {csoFileName} with {MaxCsoExeName} completed (Exit Code 0), but output ISO not found at {outputIsoPath}. Output: {outputBuilder.ToString().Trim()}");
                return (false, string.Empty, tempDir, $"{MaxCsoExeName} completed, but output ISO not found. Output: {outputBuilder.ToString().Trim()}");
            }

            LogMessage($"Successfully decompressed {csoFileName} to {outputIsoPath}");
            return (true, outputIsoPath, tempDir, string.Empty);
        }
        catch (OperationCanceledException)
        {
            LogMessage($"CSO decompression cancelled for {csoFileName}.");
            // tempDir will be cleaned up by the caller's finally block if this throws
            try
            {
                if (!process.HasExited) process.Kill(true);
            }
            catch
            {
                /*ignore*/
            }

            throw;
        }
        catch (Exception ex)
        {
            LogMessage($"Error decompressing CSO {csoFileName}: {ex.Message}");
            await ReportBugAsync($"Error decompressing CSO: {csoFileName}", ex);
            // tempDir will be cleaned up by the caller's finally block
            return (false, string.Empty, tempDir, $"Exception during CSO decompression: {ex.Message}");
        }
        finally
        {
            process?.Dispose();
        }
    }


    private async Task<(bool Success, string FilePath, string TempDir, string ErrorMessage)> ExtractArchiveAsync(string archivePath, CancellationToken token)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var extension = Path.GetExtension(archivePath).ToLowerInvariant();
        var archiveFileName = Path.GetFileName(archivePath);
        var process = new Process();

        try
        {
            Directory.CreateDirectory(tempDir);
            LogMessage($"Extracting {archiveFileName} to temporary directory: {tempDir}");

            switch (extension)
            {
                case ".zip":
                    await Task.Run(() => ZipFile.ExtractToDirectory(archivePath, tempDir, true), token);
                    break;
                case ".7z" or ".rar":
                    if (!_isSevenZipAvailable) return (false, string.Empty, tempDir, $"{SevenZipExeName} not found. Cannot extract {extension} files.");

                    process.StartInfo = new ProcessStartInfo { FileName = _sevenZipPath, Arguments = $"x \"{archivePath}\" -o\"{tempDir}\" -y", RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
                    var outputBuilder = new StringBuilder();
                    process.OutputDataReceived += (_, args) =>
                    {
                        if (args.Data != null) outputBuilder.AppendLine(args.Data);
                    };
                    process.ErrorDataReceived += (_, args) =>
                    {
                        if (args.Data != null) outputBuilder.AppendLine(args.Data);
                    };
                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    await process.WaitForExitAsync(token);
                    if (process.ExitCode != 0)
                    {
                        LogMessage($"Error extracting {archiveFileName} with {SevenZipExeName}. Exit code: {process.ExitCode}. Output: {outputBuilder}");
                        return (false, string.Empty, tempDir, $"7z.exe failed. Output: {outputBuilder.ToString().Trim()}");
                    }

                    break;
                default: return (false, string.Empty, tempDir, $"Unsupported archive type: {extension}"); // Should not happen if called correctly
            }

            var supportedFile = Directory.GetFiles(tempDir, "*.*", SearchOption.AllDirectories)
                .FirstOrDefault(static f => PrimaryTargetExtensionsInsideArchive.Contains(Path.GetExtension(f).ToLowerInvariant()));

            return supportedFile != null ? (true, supportedFile, tempDir, string.Empty) : (false, string.Empty, tempDir, "No supported primary files found in archive.");
        }
        catch (OperationCanceledException)
        {
            // tempDir will be cleaned up by the caller's finally block if this throws
            try
            {
                if (process.HasExited == false) process.Kill(true);
            }
            catch
            {
                /*ignore*/
            }

            throw;
        }
        catch (Exception ex)
        {
            LogMessage($"Error extracting archive {archiveFileName}: {ex.Message}");
            await ReportBugAsync($"Error extracting archive: {archiveFileName}", ex);
            // tempDir will be cleaned up by the caller's finally block
            return (false, string.Empty, tempDir, $"Exception during extraction: {ex.Message}");
        }
        finally
        {
            process?.Dispose();
        }
    }

    private async Task DeleteOriginalGameFilesAsync(string inputFile)
    {
        try
        {
            var filesToDelete = new List<string> { inputFile };
            var inputFileExtension = Path.GetExtension(inputFile).ToLowerInvariant();
            if (inputFileExtension == ".cue") filesToDelete.AddRange(GetReferencedFilesFromCue(inputFile));
            else if (inputFileExtension == ".gdi") filesToDelete.AddRange(GetReferencedFilesFromGdi(inputFile));

            foreach (var file in filesToDelete.Distinct(StringComparer.OrdinalIgnoreCase).Where(File.Exists))
            {
                TryDeleteFile(file, "original game file");
            }
        }
        catch (Exception ex)
        {
            LogMessage($"Failed to delete original file(s) for {Path.GetFileName(inputFile)}: {ex.Message}");
            await ReportBugAsync($"Failed to delete original file(s): {Path.GetFileName(inputFile)}", ex);
        }
    }

    private void TryDeleteFile(string filePath, string description)
    {
        try
        {
            if (!File.Exists(filePath)) return;

            File.Delete(filePath);
            LogMessage($"Deleted {description}: {Path.GetFileName(filePath)}");
        }
        catch (Exception ex)
        {
            LogMessage($"Failed to delete {description} {Path.GetFileName(filePath)}: {ex.Message}");
        }
    }

    private void TryDeleteDirectory(string dirPath, string description)
    {
        try
        {
            if (!Directory.Exists(dirPath)) return;

            Directory.Delete(dirPath, true);
            LogMessage($"Cleaned up {description}: {dirPath}");
        }
        catch (Exception ex)
        {
            LogMessage($"Failed to clean up {description} {dirPath}: {ex.Message}");
        }
    }

    private void LogOperationSummary(string operationType)
    {
        LogMessage("");
        LogMessage($"--- Batch {operationType.ToLowerInvariant()} completed. ---");
        LogMessage($"Total files processed: {_totalFilesProcessed}");
        LogMessage($"Successfully {GetPastTense(operationType)}: {_processedOkCount} files");
        if (_failedCount > 0) LogMessage($"Failed to {operationType.ToLowerInvariant()}: {_failedCount} files");

        ShowMessageBox($"Batch {operationType.ToLowerInvariant()} completed.\n\n" +
                       $"Total files processed: {_totalFilesProcessed}\n" +
                       $"Successfully {GetPastTense(operationType)}: {_processedOkCount} files\n" +
                       $"Failed: {_failedCount} files",
            $"{operationType} Complete", MessageBoxButton.OK,
            _failedCount > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }

    private string GetPastTense(string verb)
    {
        return verb.ToLowerInvariant() switch
        {
            "conversion" => "converted",
            "verification" => "verified",
            _ => verb.ToLowerInvariant() + "ed"
        };
    }


    private void ShowMessageBox(string message, string title, MessageBoxButton buttons, MessageBoxImage icon)
    {
        Dispatcher.Invoke(() => MessageBox.Show(this, message, title, buttons, icon));
    }

    private void ShowError(string message)
    {
        ShowMessageBox(message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private async Task ReportBugAsync(string message, Exception? exception = null)
    {
        try
        {
            var fullReport = new StringBuilder();
            fullReport.AppendLine("=== Bug Report ===");
            fullReport.AppendLine($"Application: {ApplicationName}");
            fullReport.AppendLine(CultureInfo.InvariantCulture, $"Version: {GetType().Assembly.GetName().Version}");
            fullReport.AppendLine(CultureInfo.InvariantCulture, $"OS: {Environment.OSVersion}");
            fullReport.AppendLine(CultureInfo.InvariantCulture, $".NET Version: {Environment.Version}");
            fullReport.AppendLine(CultureInfo.InvariantCulture, $"Date/Time: {DateTime.Now}");
            fullReport.AppendLine().AppendLine("=== Error Message ===").AppendLine(message).AppendLine();
            if (exception != null)
            {
                fullReport.AppendLine("=== Exception Details ===");
                AppendExceptionDetailsToReport(fullReport, exception);
            }

            if (LogViewer != null)
            {
                var logContent = await Dispatcher.InvokeAsync(() => LogViewer.Text);
                if (!string.IsNullOrEmpty(logContent)) fullReport.AppendLine().AppendLine("=== Application Log ===").Append(logContent);
            }

            await _bugReportService.SendBugReportAsync(fullReport.ToString());
        }
        catch
        {
            /* Silently fail reporting */
        }
    }

    private static void AppendExceptionDetailsToReport(StringBuilder sb, Exception? ex, int level = 0)
    {
        while (ex != null)
        {
            var indent = new string(' ', level * 2);
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}Type: {ex.GetType().FullName}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}Message: {ex.Message}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}Source: {ex.Source}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}StackTrace:").AppendLine(CultureInfo.InvariantCulture, $"{indent}{ex.StackTrace}");
            if (ex.InnerException == null) break;

            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}Inner Exception:");
            ex = ex.InnerException;
            level++;
        }
    }

    private void ExitMenuItem_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void AboutMenuItem_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            new AboutWindow { Owner = this }.ShowDialog();
        }
        catch (Exception ex)
        {
            LogMessage($"Error opening About window: {ex.Message}");
            Task.Run(async () => await ReportBugAsync("Error opening About window", ex));
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"Compressing\s+(?:(?:\d+/\d+)|(?:hunk\s+\d+))\s+\((?<percent>\d+[\.,]?\d*)%\)")]
    private static partial System.Text.RegularExpressions.Regex ChdmanCompressionProgressRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"ratio=(\d+[\.,]\d+)%")]
    private static partial System.Text.RegularExpressions.Regex ChdmanCompressionRatioRegex();


    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _bugReportService?.Dispose();
        _operationTimer?.Stop();
        GC.SuppressFinalize(this);
    }
}
