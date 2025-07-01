using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions; // Added for filename sanitization
using System.Windows;
using System.Windows.Controls; // Required for TabControl
using Microsoft.Win32;
using SevenZipExtractor;

namespace BatchConvertToCHD;

public partial class MainWindow : IDisposable
{
    private CancellationTokenSource _cts;

    // Bug Report API configuration and service instance are now managed by the App class.
    private static readonly char[] Separator = [' ', '\t'];

    private const string MaxCsoExeName = "maxcso.exe";
    private readonly string _maxCsoPath;
    private readonly bool _isMaxCsoAvailable;
    private readonly bool _isChdmanAvailable;

    private static readonly string[] AllSupportedInputExtensionsForConversion = { ".cue", ".iso", ".img", ".cdi", ".gdi", ".toc", ".raw", ".zip", ".7z", ".rar", ".cso" };
    private static readonly string[] ArchiveExtensions = { ".zip", ".7z", ".rar" };
    private static readonly string[] PrimaryTargetExtensionsInsideArchive = { ".cue", ".iso", ".img", ".cdi", ".gdi", ".toc", ".raw" };

    private int _currentDegreeOfParallelismForFiles = 1;

    // Statistics
    private int _totalFilesProcessed;
    private int _processedOkCount;
    private int _failedCount;
    private readonly Stopwatch _operationTimer = new();

    // For Write Speed Calculation
    private const int WriteSpeedUpdateIntervalMs = 1000;

    public MainWindow()
    {
        InitializeComponent();
        _cts = new CancellationTokenSource();

        var appDirectory = AppDomain.CurrentDomain.BaseDirectory;
        var chdmanPath = Path.Combine(appDirectory, "chdman.exe");
        _isChdmanAvailable = File.Exists(chdmanPath);

        _maxCsoPath = Path.Combine(appDirectory, MaxCsoExeName);
        _isMaxCsoAvailable = File.Exists(_maxCsoPath);

        DisplayConversionInstructionsInLog();
        ResetOperationStats();
    }

    private static string SanitizeFileName(string name)
    {
        var invalidChars = Regex.Escape(new string(Path.GetInvalidFileNameChars()));
        var invalidRegStr = string.Format(CultureInfo.InvariantCulture, @"([{0}]*\.+$)|([{0}]+)", invalidChars);
        var sanitizedName = Regex.Replace(name, invalidRegStr, "_");
        // Further replace common problematic characters not covered by GetInvalidFileNameChars but problematic for command lines
        sanitizedName = sanitizedName.Replace("…", "_ellipsis_")
            .Replace("â€¦", "_ellipsis_"); // Handle the misinterpretation too
        return sanitizedName;
    }

    private string GetSafeTempFileName(string originalFileNameWithExtension, string desiredExtensionWithoutDot, string tempDirectory)
    {
        // Use a GUID to ensure uniqueness and avoid issues with original name length or complex chars for the temp file itself.
        // The original name is still used for the *final* CHD output.
        var safeBaseName = Guid.NewGuid().ToString("N");
        return Path.Combine(tempDirectory, safeBaseName + "." + desiredExtensionWithoutDot);
    }

    private void DisplayConversionInstructionsInLog()
    {
        LogMessage($"Welcome to {AppConfig.ApplicationName}. (Conversion Mode)");
        LogMessage("");
        LogMessage("This program will convert the following formats to CHD:");
        LogMessage("- CUE+BIN files (CD images)");
        LogMessage("- ISO files (CD images)");
        LogMessage("- CDI files (CD images)");
        LogMessage("- GDI files (GD-ROM images)");
        LogMessage("- TOC files (CD images)");
        LogMessage("- IMG files (Hard disk images)");
        LogMessage("- RAW files (Raw data)");
        LogMessage("- CSO files (Compressed ISO, will be decompressed to ISO first)");
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

        LogMessage("Using SevenZipExtractor library for .7z and .rar extraction.");

        if (_isMaxCsoAvailable) LogMessage("maxcso.exe found. .cso decompression enabled for conversion.");
        else LogMessage("WARNING: maxcso.exe not found. .cso decompression will be disabled for conversion.");

        LogMessage("--- Ready for Conversion ---");
    }

    private void DisplayVerificationInstructionsInLog()
    {
        LogMessage($"Welcome to {AppConfig.ApplicationName}. (Verification Mode)");
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

        // Only clear and update if not currently busy
        if (!StartConversionButton.IsEnabled || !StartVerificationButton.IsEnabled) return;

        Application.Current.Dispatcher.InvokeAsync(() => LogViewer.Clear()); // Clear log on tab change
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

        UpdateWriteSpeedDisplay(0);
    }

    private void MoveSuccessFilesCheckBox_CheckedUnchecked(object sender, RoutedEventArgs e)
    {
        SuccessFolderPanel.Visibility = MoveSuccessFilesCheckBox.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SetControlsState(StartConversionButton.IsEnabled);
    }

    private void MoveFailedFilesCheckBox_CheckedUnchecked(object sender, RoutedEventArgs e)
    {
        FailedFolderPanel.Visibility = MoveFailedFilesCheckBox.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SetControlsState(StartConversionButton.IsEnabled);
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
    }

    private void LogMessage(string message)
    {
        var timestampedMessage = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
        Application.Current.Dispatcher.InvokeAsync(() =>
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
            await Application.Current.Dispatcher.InvokeAsync(() => LogViewer.Clear());
            DisplayConversionInstructionsInLog();

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
            }

            _cts = new CancellationTokenSource();

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
                await PerformBatchConversionAsync(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "chdman.exe"), inputFolder, outputFolder, deleteFiles, useParallelFileProcessing, _currentDegreeOfParallelismForFiles, _cts.Token);
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
                UpdateWriteSpeedDisplay(0);
                SetControlsState(true); // Log will persist here now
                LogOperationSummary("Conversion");
            }
        }
        catch (Exception ex)
        {
            _ = ReportBugAsync("Error in StartConversionButton_Click", ex);
        }
    }

    private async void StartVerificationButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await Application.Current.Dispatcher.InvokeAsync(() => LogViewer.Clear());
            DisplayVerificationInstructionsInLog();

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
            }

            _cts = new CancellationTokenSource();


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
                await PerformBatchVerificationAsync(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "chdman.exe"), inputFolder, includeSubfolders, moveSuccess, successFolder, moveFailed, failedFolder, _cts.Token);
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
                UpdateWriteSpeedDisplay(0);
                SetControlsState(true); // Log will persist here now
                LogOperationSummary("Verification");
            }
        }
        catch (Exception ex)
        {
            _ = ReportBugAsync("Error in StartVerificationButton_Click", ex);
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

        if (!enabled) return; // If the operation is starting (controls disabled), do nothing further here.

        // --- MODIFICATION: Removed log clearing and instruction display from here ---
        // This block runs when 'enabled' is true (operation finished and controls re-enabled).
        // The log should persist.
        ClearProgressDisplay();
        // if (MainTabControl.SelectedItem is TabItem selectedTab)
        // {
        //     Application.Current.Dispatcher.InvokeAsync(() => LogViewer.Clear()); // REMOVED
        //     if (selectedTab.Name == "ConvertTab") DisplayConversionInstructionsInLog(); // REMOVED
        //     else if (selectedTab.Name == "VerifyTab") DisplayVerificationInstructionsInLog(); // REMOVED
        // }
        UpdateWriteSpeedDisplay(0);
    }

    private static string? SelectFolder(string description)
    {
        var dialog = new OpenFolderDialog { Title = description };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    private async Task PerformBatchConversionAsync(string chdmanPath, string inputFolder, string outputFolder, bool deleteFiles, bool useParallelFileProcessing, int maxConcurrency, CancellationToken token)
    {
        var filesToConvert = await Task.Run(() =>
            Directory.GetFiles(inputFolder, "*.*", SearchOption.TopDirectoryOnly)
                .Where(file => AllSupportedInputExtensionsForConversion.Contains(Path.GetExtension(file).ToLowerInvariant()))
                .ToArray(), token);
        token.ThrowIfCancellationRequested();

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
            var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = maxConcurrency, CancellationToken = token };
            await Parallel.ForEachAsync(filesToConvert, parallelOptions, async (currentFile, ct) =>
            {
                var success = await ProcessSingleFileForConversionAsync(chdmanPath, currentFile, outputFolder, deleteFiles, coresPerConversion, ct);
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
                token.ThrowIfCancellationRequested();
                var success = await ProcessSingleFileForConversionAsync(chdmanPath, currentFile, outputFolder, deleteFiles, coresPerConversion, token);
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

        token.ThrowIfCancellationRequested();
        UpdateWriteSpeedDisplay(0);
    }

    private async Task<bool> ProcessSingleFileForConversionAsync(string chdmanPath, string inputFile, string outputFolder, bool deleteOriginal, int coresForChdman, CancellationToken token)
    {
        var originalInputFileName = Path.GetFileName(inputFile); // Store original name for logging and final CHD
        LogMessage($"Starting to process for conversion: {originalInputFileName}");

        string fileToProcessForChdman; // This will be the path to the sanitized temp file
        var originalFileExtension = Path.GetExtension(inputFile).ToLowerInvariant();
        var tempDir = string.Empty;
        var outputChdFile = string.Empty;

        try
        {
            token.ThrowIfCancellationRequested();

            // Determine the base name for the final CHD from the original input file
            var chdBaseName = Path.GetFileNameWithoutExtension(originalInputFileName);
            outputChdFile = Path.Combine(outputFolder, SanitizeFileName(chdBaseName) + ".chd"); // Sanitize final CHD name too

            if (originalFileExtension == ".cso")
            {
                if (!_isMaxCsoAvailable)
                {
                    LogMessage($"Skipping {originalInputFileName}: {MaxCsoExeName} is not available. This file will be marked as failed.");
                    return false;
                }

                LogMessage($"CSO file detected: {originalInputFileName}. Attempting decompression.");
                tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
                await Task.Run(() => Directory.CreateDirectory(tempDir), token);

                // Use a safe, unique name for the temporary ISO
                var tempIsoPath = GetSafeTempFileName(originalInputFileName, "iso", tempDir);

                var extractResult = await ExtractCsoAsync(inputFile, tempIsoPath, tempDir, token);
                token.ThrowIfCancellationRequested();
                if (extractResult.Success)
                {
                    fileToProcessForChdman = extractResult.FilePath; // This is tempIsoPath
                    LogMessage($"Using decompressed ISO: {Path.GetFileName(fileToProcessForChdman)} from CSO {originalInputFileName}");
                }
                else
                {
                    LogMessage($"Error decompressing CSO {originalInputFileName}: {extractResult.ErrorMessage}");
                    return false;
                }
            }
            else if (ArchiveExtensions.Contains(originalFileExtension))
            {
                LogMessage($"Archive detected: {originalInputFileName}. Attempting extraction.");
                tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()); // tempDir is set here
                await Task.Run(() => Directory.CreateDirectory(tempDir), token);

                var extractResult = await ExtractArchiveAsync(inputFile, tempDir, token);
                token.ThrowIfCancellationRequested();
                if (extractResult.Success)
                {
                    // The extracted file path might still have special chars if it was deep in an archive.
                    // Copy it to a sanitized name within our tempDir to be safe for chdman.
                    var extractedFileOriginalName = Path.GetFileName(extractResult.FilePath);
                    var extractedFileOriginalExt = Path.GetExtension(extractResult.FilePath).TrimStart('.');

                    fileToProcessForChdman = GetSafeTempFileName(extractedFileOriginalName, extractedFileOriginalExt, tempDir);

                    try
                    {
                        await Task.Run(() => File.Copy(extractResult.FilePath, fileToProcessForChdman, true), token);
                    }
                    catch (Exception ex)
                    {
                        LogMessage($"Error copying extracted file {extractedFileOriginalName} to temp path {fileToProcessForChdman}: {ex.Message}");
                        return false; // We should still process the file, but we can't copy it to chdman.
                    }

                    LogMessage($"Copied extracted file to sanitized temp path: {fileToProcessForChdman}");
                    LogMessage($"Using extracted file: {Path.GetFileName(fileToProcessForChdman)} (original in archive: {extractedFileOriginalName}) from archive {originalInputFileName}");
                }
                else
                {
                    LogMessage($"Error extracting archive {originalInputFileName}: {extractResult.ErrorMessage}");
                    return false;
                }
            }
            else
            {
                // For direct files like .iso, .cue, etc., that are not archives or CSO.
                // We should still process them from a temporary sanitized copy to be safe.
                tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
                await Task.Run(() => Directory.CreateDirectory(tempDir), token);

                var directFileExt = originalFileExtension.TrimStart('.');
                fileToProcessForChdman = GetSafeTempFileName(originalInputFileName, directFileExt, tempDir);

                try
                {
                    await Task.Run(() => File.Copy(inputFile, fileToProcessForChdman, true), token);
                }
                catch (Exception ex)
                {
                    LogMessage($"Error copying direct input file {inputFile} to temp path {fileToProcessForChdman}: {ex.Message}");
                    return false; // We should still process the file, but we can't copy it to chdman.
                }

                LogMessage($"Copied direct input file {originalInputFileName} to sanitized temp path: {fileToProcessForChdman}");
                // isArchiveOrCsoFile remains false, but tempDir is used for this temporary copy.
            }

            UpdateWriteSpeedDisplay(0);

            var outputDir = Path.GetDirectoryName(outputChdFile) ?? outputFolder;
            if (!await Task.Run(() => Directory.Exists(outputDir), token))
            {
                await Task.Run(() => Directory.CreateDirectory(outputDir), token);
            }

            token.ThrowIfCancellationRequested();

            bool conversionSuccessful;
            try
            {
                // Pass fileToProcessForChdman (sanitized temp path) to ConvertToChdAsync
                conversionSuccessful = await ConvertToChdAsync(chdmanPath, fileToProcessForChdman, outputChdFile, coresForChdman, token);
                token.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException)
            {
                LogMessage($"Conversion of {originalInputFileName} was cancelled during chdman execution.");
                if (!string.IsNullOrEmpty(outputChdFile))
                    await TryDeleteFileAsync(outputChdFile, "partially converted/cancelled CHD file", CancellationToken.None);
                throw;
            }
            catch (Exception exConv)
            {
                LogMessage($"Error during CHD conversion of {originalInputFileName}: {exConv.Message}");
                await ReportBugAsync($"CHD conversion error for {originalInputFileName}", exConv);
                if (!string.IsNullOrEmpty(outputChdFile))
                    await TryDeleteFileAsync(outputChdFile, "partially converted/failed CHD file (exception)", CancellationToken.None);
                return false;
            }
            finally
            {
                UpdateWriteSpeedDisplay(0);
            }

            if (conversionSuccessful)
            {
                LogMessage($"Successfully converted: {Path.GetFileName(fileToProcessForChdman)} (from {originalInputFileName}) to {Path.GetFileName(outputChdFile)}");
                if (!deleteOriginal) return true;
                // Delete the *original* input file, not the temp one.
                await TryDeleteFileAsync(inputFile, $"original {originalFileExtension} file: {originalInputFileName}", token);
                if (originalFileExtension is ".cue" or ".gdi") // If it was a cue/gdi, delete its referenced files too
                {
                    // We need to pass the original inputFile path to DeleteOriginalGameFilesAsync
                    // as it parses the cue/gdi from its original location.
                    await DeleteOriginalGameFilesAsync(inputFile, token);
                }

                return true;
            }
            else
            {
                LogMessage($"Failed to convert (chdman process error): {Path.GetFileName(fileToProcessForChdman)} (from {originalInputFileName})");
                if (!string.IsNullOrEmpty(outputChdFile))
                    await TryDeleteFileAsync(outputChdFile, "partially converted/failed CHD file (chdman error)", CancellationToken.None);
                return false;
            }
        }
        catch (OperationCanceledException)
        {
            LogMessage($"Conversion processing cancelled for {originalInputFileName}.");
            UpdateWriteSpeedDisplay(0);
            throw;
        }
        catch (Exception ex)
        {
            LogMessage($"Error processing file {originalInputFileName} for conversion: {ex.Message}");
            await ReportBugAsync($"Error processing file for conversion: {originalInputFileName}", ex);
            if (!string.IsNullOrEmpty(outputChdFile))
            {
                await TryDeleteFileAsync(outputChdFile, "partially converted/failed CHD file (general error)", CancellationToken.None);
            }

            UpdateWriteSpeedDisplay(0);
            return false;
        }
        finally
        {
            // Cleanup tempDir which now holds the sanitized copy (fileToProcessForChdman)
            // or was used for CSO/archive extraction.
            if (!string.IsNullOrEmpty(tempDir)) // tempDir is created for all paths now
            {
                bool tempDirExists;
                try
                {
                    tempDirExists = Directory.Exists(tempDir);
                }
                catch (Exception ex)
                {
                    LogMessage($"Error checking existence of temp directory {tempDir} for cleanup: {ex.Message}");
                    tempDirExists = true;
                }

                if (tempDirExists)
                {
                    await TryDeleteDirectoryAsync(tempDir, "temporary processing directory (final cleanup)", CancellationToken.None);
                }
            }
        }
    }

    private async Task PerformBatchVerificationAsync(string chdmanPath, string inputFolder, bool includeSubfolders, bool moveSuccess, string successFolder, bool moveFailed, string failedFolder, CancellationToken token)
    {
        var searchOption = includeSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var filesToVerify = await Task.Run(() => Directory.GetFiles(inputFolder, "*.chd", searchOption), token);
        token.ThrowIfCancellationRequested();

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
            token.ThrowIfCancellationRequested();
            var fileName = Path.GetFileName(chdFile);
            UpdateProgressDisplay(filesActuallyProcessedCount + 1, _totalFilesProcessed, fileName, "Verifying");

            var isValid = await VerifyChdAsync(chdmanPath, chdFile, token);
            token.ThrowIfCancellationRequested();

            if (isValid)
            {
                LogMessage($"✓ Verification successful: {fileName}");
                _processedOkCount++;
                if (moveSuccess && !string.IsNullOrEmpty(successFolder))
                {
                    await MoveVerifiedFileAsync(chdFile, successFolder, inputFolder, includeSubfolders, "successfully verified", token);
                }
            }
            else
            {
                LogMessage($"✗ Verification failed: {fileName}");
                _failedCount++;
                if (moveFailed && !string.IsNullOrEmpty(failedFolder))
                {
                    await MoveVerifiedFileAsync(chdFile, failedFolder, inputFolder, includeSubfolders, "failed verification", token);
                }
            }

            token.ThrowIfCancellationRequested();
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
            token.ThrowIfCancellationRequested();
            string targetDir;
            if (maintainSubfolders && !string.IsNullOrEmpty(Path.GetDirectoryName(sourceFile)) && Path.GetDirectoryName(sourceFile) != baseInputFolder)
            {
                var relativeDir = Path.GetRelativePath(baseInputFolder, Path.GetDirectoryName(sourceFile) ?? string.Empty);
                targetDir = Path.Combine(destinationParentFolder, relativeDir);
                destinationFile = Path.Combine(targetDir, fileName);
            }
            else
            {
                targetDir = destinationParentFolder;
                destinationFile = Path.Combine(destinationParentFolder, fileName);
            }

            if (!await Task.Run(() => Directory.Exists(targetDir), token))
            {
                await Task.Run(() => Directory.CreateDirectory(targetDir), token);
            }

            token.ThrowIfCancellationRequested();

            if (await Task.Run(() => File.Exists(destinationFile), token))
            {
                LogMessage($"Cannot move {fileName}: Destination file already exists at {destinationFile}. Skipping move.");
                return;
            }

            token.ThrowIfCancellationRequested();

            await Task.Run(() => File.Move(sourceFile, destinationFile), token);
            LogMessage($"Moved {fileName} ({moveReason}) to {destinationFile}");
        }
        catch (OperationCanceledException)
        {
            LogMessage($"Move operation for {fileName} cancelled.");
            throw;
        }
        catch (Exception ex)
        {
            LogMessage($"Error moving {fileName} to {destinationParentFolder}: {ex.Message}");
            await ReportBugAsync($"Error moving verified file {fileName}", ex);
        }
    }


    private async Task<bool> ConvertToChdAsync(string chdmanPath, string sanitizedInputFile, string outputChdFile, int coresForChdman, CancellationToken token)
    {
        // sanitizedInputFile is the path to the file with a safe name in a temp directory
        // outputChdFile is the path to the final CHD, named based on the original input
        var process = new Process();
        try
        {
            token.ThrowIfCancellationRequested();
            var command = "createcd";
            var extension = Path.GetExtension(sanitizedInputFile).ToLowerInvariant(); // Use extension of the temp file
            switch (extension)
            {
                case ".img": command = "createhd"; break;
                case ".raw": command = "createraw"; break;
                // .iso, .cue, .gdi, .cdi, .toc (from sanitized temp) will use createcd
            }

            coresForChdman = Math.Max(1, coresForChdman);

            LogMessage($"CHDMAN Convert: Using command '{command}' with {coresForChdman} core(s) for {Path.GetFileName(sanitizedInputFile)} -> {Path.GetFileName(outputChdFile)}.");
            var arguments = $"{command} -i \"{sanitizedInputFile}\" -o \"{outputChdFile}\" -f -np {coresForChdman}";
            // ... (rest of ConvertToChdAsync remains the same, using sanitizedInputFile for -i) ...
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
                LogMessage($"[CHDMAN CONVERT STDERR] {args.Data}"); // Log the raw error from chdman
                UpdateConversionProgressFromChdman(args.Data);
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var lastSpeedCheckTime = DateTime.UtcNow;
            long lastFileSize = 0;
            if (await Task.Run(() => File.Exists(outputChdFile), token))
            {
                lastFileSize = await Task.Run(() => new FileInfo(outputChdFile).Length, token);
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

                    token.ThrowIfCancellationRequested();
                }

                await Task.Delay(WriteSpeedUpdateIntervalMs, token);

                if (process.HasExited || token.IsCancellationRequested) break;

                try
                {
                    if (await Task.Run(() => File.Exists(outputChdFile), token))
                    {
                        var currentFileSize = await Task.Run(() => new FileInfo(outputChdFile).Length, token);
                        var currentTime = DateTime.UtcNow;
                        var timeDelta = currentTime - lastSpeedCheckTime;

                        if (timeDelta.TotalSeconds > 0)
                        {
                            var bytesDelta = currentFileSize - lastFileSize;
                            var speed = (bytesDelta / timeDelta.TotalSeconds) / (1024.0 * 1024.0);
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

            await process.WaitForExitAsync(token);
            return process.ExitCode == 0;
        }
        catch (OperationCanceledException)
        {
            LogMessage("Conversion operation was canceled by user.");
            try
            {
                if (!process.HasExited) process.Kill(true);
            }
            catch
            {
                // ignore
            }

            throw;
        }
        finally
        {
            process?.Dispose();
        }
    }

    private async Task<bool> VerifyChdAsync(string chdmanPath, string chdFile, CancellationToken token)
    {
        using var process = new Process();
        try
        {
            token.ThrowIfCancellationRequested();
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
                // ignore
            }

            throw;
        }
        catch (Exception ex)
        {
            LogMessage($"Error verifying file {Path.GetFileName(chdFile)}: {ex.Message}");
            await ReportBugAsync($"Error verifying file: {Path.GetFileName(chdFile)} with chdman", ex);
            return false;
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
        UpdateWriteSpeedDisplay(0);
        ClearProgressDisplay();
    }

    private void UpdateStatsDisplay()
    {
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            TotalFilesValue.Text = _totalFilesProcessed.ToString(CultureInfo.InvariantCulture);
            SuccessValue.Text = _processedOkCount.ToString(CultureInfo.InvariantCulture);
            FailedValue.Text = _failedCount.ToString(CultureInfo.InvariantCulture);
        });
    }

    private void UpdateProcessingTimeDisplay()
    {
        var elapsed = _operationTimer.Elapsed;
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            ProcessingTimeValue.Text = $@"{elapsed:hh\:mm\:ss}";
        });
    }

    private void UpdateWriteSpeedDisplay(double speedInMBps)
    {
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            WriteSpeedValue.Text = $"{speedInMBps:F1} MB/s";
        });
    }

    private void UpdateProgressDisplay(int current, int total, string currentFileName, string operationVerb)
    {
        var percentage = total == 0 ? 0 : (double)current / total * 100;
        Application.Current.Dispatcher.InvokeAsync(() =>
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
        Application.Current.Dispatcher.InvokeAsync(() =>
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
                /* Potentially log ratio or update UI if needed */
            }
        }
        catch (Exception ex)
        {
            LogMessage($"Error updating chdman conversion progress detail: {ex.Message}");
        }
    }

    private async Task<List<string>> GetReferencedFilesFromCueAsync(string cuePath, CancellationToken token)
    {
        var referencedFiles = new List<string>();
        var cueDir = Path.GetDirectoryName(cuePath) ?? string.Empty;
        try
        {
            var lines = await File.ReadAllLinesAsync(cuePath, Encoding.UTF8, token);
            token.ThrowIfCancellationRequested();
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
        catch (OperationCanceledException)
        {
            LogMessage("Process was canceled by user.");
            throw;
        }
        catch (Exception ex)
        {
            LogMessage($"Error parsing CUE file {Path.GetFileName(cuePath)}: {ex.Message}");
            _ = ReportBugAsync($"Error parsing CUE file: {Path.GetFileName(cuePath)}", ex);
        }

        return referencedFiles;
    }

    private async Task<List<string>> GetReferencedFilesFromGdiAsync(string gdiPath, CancellationToken token)
    {
        var referencedFiles = new List<string>();
        var gdiDir = Path.GetDirectoryName(gdiPath) ?? string.Empty;
        try
        {
            var lines = await File.ReadAllLinesAsync(gdiPath, Encoding.UTF8, token);
            token.ThrowIfCancellationRequested();
            for (var i = 1; i < lines.Length; i++)
            {
                var trimmedLine = lines[i].Trim();
                if (string.IsNullOrWhiteSpace(trimmedLine)) continue;

                var parts = trimmedLine.Split(Separator, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 5) continue;

                referencedFiles.Add(Path.Combine(gdiDir, parts[4].Trim('"')));
            }
        }
        catch (OperationCanceledException)
        {
            LogMessage("Process was canceled by user.");
            throw;
        }
        catch (Exception ex)
        {
            LogMessage($"Error parsing GDI file {Path.GetFileName(gdiPath)}: {ex.Message}");
            _ = ReportBugAsync($"Error parsing GDI file: {Path.GetFileName(gdiPath)}", ex);
        }

        return referencedFiles;
    }

    private async Task<(bool Success, string FilePath, string TempDir, string ErrorMessage)> ExtractCsoAsync(string originalCsoPath, string tempOutputIsoPath, string tempDirectoryRoot, CancellationToken token)
    {
        // tempOutputIsoPath is the full path for the sanitized temporary ISO
        // tempDirectoryRoot is the parent temp folder (e.g., C:\Users\...\Temp\randomname\)
        var csoFileNameForLog = Path.GetFileName(originalCsoPath);
        var process = new Process();

        try
        {
            token.ThrowIfCancellationRequested();
            // tempDirectoryRoot is already created by the caller (ProcessSingleFileForConversionAsync)
            LogMessage($"Decompressing {csoFileNameForLog} to temporary ISO: {tempOutputIsoPath}");

            process.StartInfo = new ProcessStartInfo
            {
                FileName = _maxCsoPath,
                Arguments = $"--decompress \"{originalCsoPath}\" -o \"{tempOutputIsoPath}\"",
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
            };

            var outputBuilder = new StringBuilder();
            process.OutputDataReceived += (_, args) =>
            {
                if (args.Data != null) outputBuilder.AppendLine(args.Data);
            };
            process.ErrorDataReceived += (_, args) =>
            {
                if (args.Data == null) return;

                outputBuilder.AppendLine(args.Data);
                LogMessage($"[MAXCSO STDERR] {args.Data}");
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var lastSpeedCheckTime = DateTime.UtcNow;
            long lastFileSize = 0;

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

                    token.ThrowIfCancellationRequested();
                }

                await Task.Delay(WriteSpeedUpdateIntervalMs, token);
                if (process.HasExited || token.IsCancellationRequested) break;

                try
                {
                    if (await Task.Run(() => File.Exists(tempOutputIsoPath), token))
                    {
                        var currentFileSize = await Task.Run(() => new FileInfo(tempOutputIsoPath).Length, token);
                        var currentTime = DateTime.UtcNow;
                        var timeDelta = currentTime - lastSpeedCheckTime;
                        if (timeDelta.TotalSeconds > 0)
                        {
                            var bytesDelta = currentFileSize - lastFileSize;
                            var speed = (bytesDelta / timeDelta.TotalSeconds) / (1024.0 * 1024.0);
                            UpdateWriteSpeedDisplay(speed);
                        }

                        lastFileSize = currentFileSize;
                        lastSpeedCheckTime = currentTime;
                    }
                }
                catch (FileNotFoundException)
                {
                    /* File might not be created yet */
                }
                catch (Exception ex)
                {
                    LogMessage($"CSO decompression write speed monitoring error: {ex.Message}");
                }
            }

            await process.WaitForExitAsync(token);

            if (process.ExitCode != 0)
            {
                LogMessage($"Error decompressing {csoFileNameForLog} with {MaxCsoExeName}. Exit code: {process.ExitCode}. Output: {outputBuilder.ToString().Trim()}");
                return (false, string.Empty, tempDirectoryRoot, $"{MaxCsoExeName} failed. Exit code: {process.ExitCode}. Output: {outputBuilder.ToString().Trim()}");
            }

            if (!await Task.Run(() => File.Exists(tempOutputIsoPath), token))
            {
                LogMessage($"Decompression of {csoFileNameForLog} with {MaxCsoExeName} completed (Exit Code 0), but output ISO not found at {tempOutputIsoPath}. Output: {outputBuilder.ToString().Trim()}");
                return (false, string.Empty, tempDirectoryRoot, $"{MaxCsoExeName} completed, but output ISO not found. Output: {outputBuilder.ToString().Trim()}");
            }

            LogMessage($"Successfully decompressed {csoFileNameForLog} to {tempOutputIsoPath}");
            return (true, tempOutputIsoPath, tempDirectoryRoot, string.Empty); // Return tempOutputIsoPath as FilePath
        }
        catch (OperationCanceledException)
        {
            LogMessage("CSO extraction was canceled by user.");
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
        finally
        {
            process?.Dispose();
        }
    }

    private async Task<(bool Success, string FilePath, string TempDir, string ErrorMessage)> ExtractArchiveAsync(
        string originalArchivePath, string tempDirectoryRoot, CancellationToken token)
    {
        var extension = Path.GetExtension(originalArchivePath).ToLowerInvariant();
        var archiveFileNameForLog = Path.GetFileName(originalArchivePath);

        try
        {
            token.ThrowIfCancellationRequested();
            LogMessage($"Extracting {archiveFileNameForLog} to temporary directory: {tempDirectoryRoot}");

            switch (extension)
            {
                case ".zip":
                    LogMessage($"Extracting ZIP {archiveFileNameForLog}");
                    await Task.Run(() =>
                        ZipFile.ExtractToDirectory(originalArchivePath, tempDirectoryRoot, true), token);
                    break;

                case ".7z":
                case ".rar":
                    LogMessage($"Extracting {extension.ToUpperInvariant()} {archiveFileNameForLog} using SevenZipExtractor");
                    await Task.Run(() =>
                    {
                        using var archiveFile = new ArchiveFile(originalArchivePath);
                        archiveFile.Extract(tempDirectoryRoot);
                    }, token);
                    break;

                default:
                    return (false, string.Empty, tempDirectoryRoot, $"Unsupported archive type: {extension}");
            }

            token.ThrowIfCancellationRequested();

            // Search for the primary target file within the tempDirectoryRoot
            var foundPrimaryFilePathInTemp = await Task.Run(() =>
                Directory.GetFiles(tempDirectoryRoot, "*.*", SearchOption.AllDirectories)
                    .FirstOrDefault(static f => PrimaryTargetExtensionsInsideArchive.Contains(
                        Path.GetExtension(f).ToLowerInvariant())), token);

            return foundPrimaryFilePathInTemp != null
                ? (true, foundPrimaryFilePathInTemp, tempDirectoryRoot, string.Empty)
                : (false, string.Empty, tempDirectoryRoot, "No supported primary files found in archive.");
        }
        catch (OperationCanceledException)
        {
            LogMessage("Extraction operation was canceled by user.");
            throw;
        }
        catch (Exception ex)
        {
            return (false, string.Empty, tempDirectoryRoot,
                $"Error extracting archive: {ex.Message}");
        }
    }

    private async Task DeleteOriginalGameFilesAsync(string inputFile, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            var filesToDelete = new List<string> { inputFile };
            var inputFileExtension = Path.GetExtension(inputFile).ToLowerInvariant();

            if (inputFileExtension == ".cue") filesToDelete.AddRange(await GetReferencedFilesFromCueAsync(inputFile, token));
            else if (inputFileExtension == ".gdi") filesToDelete.AddRange(await GetReferencedFilesFromGdiAsync(inputFile, token));
            token.ThrowIfCancellationRequested();

            foreach (var file in filesToDelete.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                await TryDeleteFileAsync(file, "original game file", token);
                token.ThrowIfCancellationRequested();
            }
        }
        catch (OperationCanceledException)
        {
            LogMessage("Process was canceled by user.");
            throw;
        }
        catch (Exception ex)
        {
            LogMessage($"Failed to delete original file(s) for {Path.GetFileName(inputFile)}: {ex.Message}");
            await ReportBugAsync($"Failed to delete original file(s): {Path.GetFileName(inputFile)}", ex);
        }
    }

    private async Task TryDeleteFileAsync(string filePath, string description, CancellationToken token)
    {
        try
        {
            // For cleanup, token might be CancellationToken.None if called from a failure path
            if (token != CancellationToken.None) token.ThrowIfCancellationRequested();

            if (!await Task.Run(() => File.Exists(filePath), token == CancellationToken.None ? new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token : token)) return;

            if (token != CancellationToken.None) token.ThrowIfCancellationRequested();
            await Task.Run(() => File.Delete(filePath), token == CancellationToken.None ? new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token : token);
            LogMessage($"Deleted {description}: {Path.GetFileName(filePath)}");
        }
        catch (OperationCanceledException) when (token != CancellationToken.None)
        {
            LogMessage("Process was canceled by user.");
            throw;
        } // Only rethrow if it's not from our short timeout token
        catch (Exception ex)
        {
            LogMessage($"Failed to delete {description} {Path.GetFileName(filePath)}: {ex.Message}");
        }
    }

    private async Task TryDeleteDirectoryAsync(string dirPath, string description, CancellationToken token)
    {
        try
        {
            if (token != CancellationToken.None) token.ThrowIfCancellationRequested();
            if (!await Task.Run(() => Directory.Exists(dirPath), token == CancellationToken.None ? new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token : token)) return;

            if (token != CancellationToken.None) token.ThrowIfCancellationRequested();
            await Task.Run(() => Directory.Delete(dirPath, true), token == CancellationToken.None ? new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token : token);
            LogMessage($"Cleaned up {description}: {dirPath}");
        }
        catch (OperationCanceledException) when (token != CancellationToken.None)
        {
            LogMessage("Process was canceled by user.");
            throw;
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

        Application.Current.Dispatcher.InvokeAsync(() =>
            ShowMessageBox($"Batch {operationType.ToLowerInvariant()} completed.\n\n" +
                           $"Total files processed: {_totalFilesProcessed}\n" +
                           $"Successfully {GetPastTense(operationType)}: {_processedOkCount} files\n" +
                           $"Failed: {_failedCount} files",
                $"{operationType} Complete", MessageBoxButton.OK,
                _failedCount > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information));
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
        MessageBox.Show(this, message, title, buttons, icon);
    }

    private void ShowError(string message)
    {
        Application.Current.Dispatcher.InvokeAsync(() =>
            ShowMessageBox(message, "Error", MessageBoxButton.OK, MessageBoxImage.Error));
    }

    private async Task ReportBugAsync(string message, Exception? exception = null)
    {
        try
        {
            var fullReport = new StringBuilder();
            fullReport.AppendLine("=== Bug Report ===");
            fullReport.AppendLine($"Application: {AppConfig.ApplicationName}");
            fullReport.AppendLine(CultureInfo.InvariantCulture, $"Version: {GetType().Assembly.GetName().Version}");
            fullReport.AppendLine(CultureInfo.InvariantCulture, $"OS: {Environment.OSVersion}");
            fullReport.AppendLine(CultureInfo.InvariantCulture, $".NET Version: {Environment.Version}");
            fullReport.AppendLine(CultureInfo.InvariantCulture, $"Date/Time: {DateTime.Now}");
            fullReport.AppendLine().AppendLine("=== Error Message ===").AppendLine(message).AppendLine();
            if (exception != null)
            {
                fullReport.AppendLine("=== Exception Details ===");
                App.AppendExceptionDetails(fullReport, exception);
            }

            if (LogViewer != null)
            {
                var logContent = await Application.Current.Dispatcher.InvokeAsync(() => LogViewer.Text);
                if (!string.IsNullOrEmpty(logContent)) fullReport.AppendLine().AppendLine("=== Application Log ===").Append(logContent);
            }

            if (App.SharedBugReportService != null)
            {
                await App.SharedBugReportService.SendBugReportAsync(fullReport.ToString());
            }
        }
        catch
        {
            /* Silently fail reporting */
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
            _ = ReportBugAsync("Error opening About window", ex);
        }
    }

    [GeneratedRegex(@"Compressing\s+(?:(?:\d+/\d+)|(?:hunk\s+\d+))\s+\((?<percent>\d+[\.,]?\d*)%\)")]
    private static partial Regex ChdmanCompressionProgressRegex();

    [GeneratedRegex(@"ratio=(\d+[\.,]\d+)%")]
    private static partial Regex ChdmanCompressionRatioRegex();

    /// <inheritdoc />
    /// <summary>
    /// Releases all resources used by the current instance of the MainWindow class.
    /// </summary>
    /// <remarks>
    /// This method disposes of the System.Threading.CancellationTokenSource and any other disposable dependencies.
    /// It also stops the operation timer and suppresses finalization for this instance.
    /// </remarks>
    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _operationTimer?.Stop();
        GC.SuppressFinalize(this);
    }
}
