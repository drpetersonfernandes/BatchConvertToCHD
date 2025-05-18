using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows;
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

    private const string SevenZipExeName = "7z.exe"; // Or 7za.exe if you prefer standalone
    private readonly string _sevenZipPath;
    private readonly bool _isSevenZipAvailable;

    private static readonly string[] AllSupportedInputExtensions = { ".cue", ".iso", ".img", ".cdi", ".gdi", ".toc", ".raw", ".zip", ".7z", ".rar" };
    private static readonly string[] ArchiveExtensions = { ".zip", ".7z", ".rar" };
    private static readonly string[] PrimaryTargetExtensionsInsideArchive = { ".cue", ".iso", ".img", ".cdi", ".gdi", ".toc", ".raw" };

    private int _currentDegreeOfParallelismForFiles = 1;

    public MainWindow()
    {
        InitializeComponent();
        _cts = new CancellationTokenSource();

        // Initialize the bug report service
        _bugReportService = new BugReportService(BugReportApiUrl, BugReportApiKey, ApplicationName);

        LogMessage("Welcome to the Batch Convert to CHD.");
        LogMessage("");
        LogMessage("This program will convert the following formats to CHD:");
        LogMessage("- CUE+BIN files (CD images)");
        LogMessage("- ISO files (CD images)");
        LogMessage("- CDI files (CD images)");
        LogMessage("- GDI files (GD-ROM images)");
        LogMessage("- TOC files (CD images)");
        LogMessage("- IMG files (Hard disk images)");
        LogMessage("- RAW files (Raw data)");
        LogMessage("- ZIP, 7Z, RAR files (containing any of the above formats)");
        LogMessage("");
        LogMessage("Please follow these steps:");
        LogMessage("1. Select the input folder containing files to convert");
        LogMessage("2. Select the output folder where CHD files will be saved");
        LogMessage("3. Choose whether to delete original files after conversion");
        LogMessage("4. Optionally, enable parallel processing for faster conversion of multiple files");
        LogMessage("5. Click 'Start Conversion' to begin the process");
        LogMessage("");

        // Verify chdman.exe exists
        var appDirectory = AppDomain.CurrentDomain.BaseDirectory;
        var chdmanPath = Path.Combine(appDirectory, "chdman.exe");

        if (File.Exists(chdmanPath))
        {
            LogMessage("chdman.exe found in the application directory.");
        }
        else
        {
            LogMessage("WARNING: chdman.exe not found in the application directory!");
            LogMessage("Please ensure chdman.exe is in the same folder as this application.");
            Task.Run(async () => await ReportBugAsync("chdman.exe not found in the application directory. This will prevent the application from functioning correctly."));
        }

        // Verify 7z.exe exists
        _sevenZipPath = Path.Combine(appDirectory, SevenZipExeName);
        if (File.Exists(_sevenZipPath))
        {
            _isSevenZipAvailable = true;
            LogMessage($"{SevenZipExeName} found. .7z and .rar extraction enabled.");
        }
        else
        {
            _isSevenZipAvailable = false;
            LogMessage($"WARNING: {SevenZipExeName} not found. .7z and .rar extraction will be disabled.");
        }
    }

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        _cts.Cancel();
        // Let the application shut down gracefully if tasks are running.
        // Consider awaiting tasks or providing a timeout if needed.
        // For simplicity, direct shutdown.
        Application.Current.Shutdown();
        Environment.Exit(0); // Force exit if shutdown doesn't complete quickly
    }

    private void LogMessage(string message)
    {
        var timestampedMessage = $"[{DateTime.Now:HH:mm:ss.fff}] {message}"; // Added milliseconds for better parallel logging

        Application.Current.Dispatcher.Invoke((Action)(() =>
        {
            LogViewer.AppendText($"{timestampedMessage}{Environment.NewLine}");
            LogViewer.ScrollToEnd();
        }));
    }

    private void BrowseInputButton_Click(object sender, RoutedEventArgs e)
    {
        var inputFolder = SelectFolder("Select the folder containing files to convert");
        if (string.IsNullOrEmpty(inputFolder)) return;

        InputFolderTextBox.Text = inputFolder;
        LogMessage($"Input folder selected: {inputFolder}");
    }

    private void BrowseOutputButton_Click(object sender, RoutedEventArgs e)
    {
        var outputFolder = SelectFolder("Select the output folder where CHD files will be saved");
        if (string.IsNullOrEmpty(outputFolder)) return;

        OutputFolderTextBox.Text = outputFolder;
        LogMessage($"Output folder selected: {outputFolder}");
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var appDirectory = AppDomain.CurrentDomain.BaseDirectory;
            var chdmanPath = Path.Combine(appDirectory, "chdman.exe");

            if (!File.Exists(chdmanPath))
            {
                LogMessage("Error: chdman.exe not found in the application folder.");
                ShowError("chdman.exe is missing from the application folder. Please ensure it's in the same directory as this application.");
                await ReportBugAsync("chdman.exe not found when trying to start conversion",
                    new FileNotFoundException("The required chdman.exe file was not found.", chdmanPath));
                return;
            }

            var inputFolder = InputFolderTextBox.Text;
            var outputFolder = OutputFolderTextBox.Text;
            var deleteFiles = DeleteFilesCheckBox.IsChecked ?? false;
            var useParallelFileProcessing = ParallelProcessingCheckBox.IsChecked ?? false;

            _currentDegreeOfParallelismForFiles = useParallelFileProcessing ? Environment.ProcessorCount : 1;
            if (_currentDegreeOfParallelismForFiles <= 0)
            {
                _currentDegreeOfParallelismForFiles = 1;
            }

            if (string.IsNullOrEmpty(inputFolder))
            {
                LogMessage("Error: No input folder selected.");
                ShowError("Please select the input folder containing files to convert.");
                return;
            }

            if (string.IsNullOrEmpty(outputFolder))
            {
                LogMessage("Error: No output folder selected.");
                ShowError("Please select the output folder where CHD files will be saved.");
                return;
            }

            if (_cts.IsCancellationRequested)
            {
                _cts.Dispose();
                _cts = new CancellationTokenSource();
            }

            ClearProgressDisplay();
            SetControlsState(false);

            LogMessage("Starting batch conversion process...");
            LogMessage($"Using chdman.exe: {chdmanPath}");
            LogMessage($"Input folder: {inputFolder}");
            LogMessage($"Output folder: {outputFolder}");
            LogMessage($"Delete original files: {deleteFiles}");
            LogMessage($"Parallel file processing: {useParallelFileProcessing} (Max concurrency: {_currentDegreeOfParallelismForFiles})");

            try
            {
                await PerformBatchConversionAsync(chdmanPath, inputFolder, outputFolder, deleteFiles, useParallelFileProcessing, _currentDegreeOfParallelismForFiles);
            }
            catch (OperationCanceledException)
            {
                LogMessage("Operation was canceled by user.");
            }
            catch (Exception ex)
            {
                LogMessage($"Error: {ex.Message}");
                await ReportBugAsync("Error during batch conversion process", ex);
            }
            finally
            {
                SetControlsState(true);
            }
        }
        catch (Exception ex)
        {
            await ReportBugAsync("Error during batch conversion process", ex);
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _cts.Cancel();
        LogMessage("Cancellation requested. Waiting for current operation(s) to complete...");
    }

    private void SetControlsState(bool enabled)
    {
        InputFolderTextBox.IsEnabled = enabled;
        OutputFolderTextBox.IsEnabled = enabled;
        BrowseInputButton.IsEnabled = enabled;
        BrowseOutputButton.IsEnabled = enabled;
        DeleteFilesCheckBox.IsEnabled = enabled;
        ParallelProcessingCheckBox.IsEnabled = enabled;
        StartButton.IsEnabled = enabled;

        ProgressBar.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        CancelButton.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        ProgressText.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;

        if (enabled)
        {
            ClearProgressDisplay();
        }
    }

    private static string? SelectFolder(string description)
    {
        var dialog = new OpenFolderDialog
        {
            Title = description
        };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    private async Task PerformBatchConversionAsync(string chdmanPath, string inputFolder, string outputFolder, bool deleteFiles, bool useParallelFileProcessing, int degreeOfParallelismForFiles)
    {
        try
        {
            LogMessage("Preparing for batch conversion...");
            var files = Directory.GetFiles(inputFolder, "*.*", SearchOption.TopDirectoryOnly)
                .Where(static file => AllSupportedInputExtensions.Contains(Path.GetExtension(file).ToLowerInvariant()))
                .ToArray();

            LogMessage($"Found {files.Length} files to process.");
            if (files.Length == 0)
            {
                LogMessage("No supported files found in the input folder.");
                return;
            }

            ProgressBar.Maximum = files.Length;
            ProgressBar.Value = 0;

            var successCount = 0;
            var failureCount = 0;
            var filesProcessedCount = 0; // For progress reporting in parallel mode

            if (useParallelFileProcessing && files.Length > 1)
            {
                LogMessage($"Using parallel processing with up to {degreeOfParallelismForFiles} concurrent tasks.");
                var semaphore = new SemaphoreSlim(degreeOfParallelismForFiles);
                var tasks = new List<Task>();

                foreach (var inputFile in files)
                {
                    if (_cts.Token.IsCancellationRequested) break;

                    await semaphore.WaitAsync(_cts.Token);
                    if (_cts.Token.IsCancellationRequested)
                    {
                        semaphore.Release();
                        break;
                    }

                    tasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            var fileName = Path.GetFileName(inputFile);
                            var currentFileNumber = Interlocked.Increment(ref filesProcessedCount);

                            LogMessage($"[Parallel {currentFileNumber}/{files.Length}] Starting: {fileName}");
                            UpdateProgressStatus(currentFileNumber, files.Length, fileName);

                            var success = await ProcessFileAsync(chdmanPath, inputFile, outputFolder, deleteFiles, degreeOfParallelismForFiles);
                            if (success)
                            {
                                LogMessage($"[Parallel {currentFileNumber}/{files.Length}] Successful: {fileName}");
                                Interlocked.Increment(ref successCount);
                            }
                            else
                            {
                                LogMessage($"[Parallel {currentFileNumber}/{files.Length}] Failed: {fileName}");
                                Interlocked.Increment(ref failureCount);
                            }

                            Application.Current.Dispatcher.Invoke(() => ProgressBar.Value = filesProcessedCount);
                        }
                        catch (OperationCanceledException)
                        {
                            LogMessage($"Operation canceled for file (parallel): {Path.GetFileName(inputFile)}");
                        }
                        catch (Exception ex)
                        {
                            LogMessage($"Error processing file (parallel): {Path.GetFileName(inputFile)} - {ex.Message}");
                            Interlocked.Increment(ref failureCount);
                            await ReportBugAsync($"Error during parallel processing of {Path.GetFileName(inputFile)}", ex);
                        }
                        finally
                        {
                            semaphore.Release();
                        }
                    }, _cts.Token));
                }

                await Task.WhenAll(tasks);
            }
            else // Sequential processing
            {
                LogMessage("Using sequential processing.");
                foreach (var t in files)
                {
                    if (_cts.Token.IsCancellationRequested)
                    {
                        LogMessage("Operation canceled by user.");
                        break;
                    }

                    var fileName = Path.GetFileName(t);
                    Interlocked.Increment(ref filesProcessedCount);

                    UpdateProgressStatus(filesProcessedCount, files.Length, fileName);
                    LogMessage($"[{filesProcessedCount}/{files.Length}] Processing: {fileName}");

                    var success = await ProcessFileAsync(chdmanPath, t, outputFolder, deleteFiles, 1); // degreeOfParallelism is 1 for sequential
                    if (success)
                    {
                        LogMessage($"Conversion successful: {fileName}");
                        successCount++;
                    }
                    else
                    {
                        LogMessage($"Conversion failed: {fileName}");
                        failureCount++;
                    }

                    ProgressBar.Value = filesProcessedCount;
                }
            }

            LogMessage("");
            LogMessage("Batch conversion completed.");
            LogMessage($"Successfully converted: {successCount} files");
            if (failureCount > 0) LogMessage($"Failed to convert: {failureCount} files");

            ShowMessageBox($"Batch conversion completed.\n\nSuccessfully converted: {successCount} files\nFailed to convert: {failureCount} files",
                "Conversion Complete", MessageBoxButton.OK, failureCount > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            LogMessage("Batch conversion operation was canceled.");
        }
        catch (Exception ex)
        {
            LogMessage($"Error during batch conversion: {ex.Message}");
            ShowError($"Error during batch conversion: {ex.Message}");
            await ReportBugAsync("Error during batch conversion operation", ex);
        }
    }

    private void UpdateProgressStatus(int current, int total, string currentFile)
    {
        var percentage = total > 0 ? (double)current / total * 100 : 0;
        Application.Current.Dispatcher.Invoke(() =>
        {
            ProgressBar.Value = current;
            ProgressBar.Maximum = total;
            ProgressBar.Visibility = Visibility.Visible;
            ProgressText.Text = $"Processing file {current} of {total}: {Path.GetFileName(currentFile)} ({percentage:F1}%)";
            ProgressText.Visibility = Visibility.Visible;
        });
    }

    private void ClearProgressDisplay()
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            ProgressBar.Visibility = Visibility.Collapsed;
            ProgressText.Text = string.Empty;
            ProgressText.Visibility = Visibility.Collapsed;
        });
    }

    private List<string> GetReferencedFilesFromCue(string cuePath)
    {
        var referencedFiles = new List<string>();
        var cueDir = Path.GetDirectoryName(cuePath) ?? string.Empty;
        try
        {
            LogMessage($"Parsing CUE file: {Path.GetFileName(cuePath)}");
            var lines = File.ReadAllLines(cuePath);
            foreach (var line in lines)
            {
                var trimmedLine = line.Trim();
                if (!trimmedLine.StartsWith("FILE ", StringComparison.OrdinalIgnoreCase)) continue;

                var parts = trimmedLine.Split('"');
                if (parts.Length < 2) continue;

                var fileName = parts[1];
                var filePath = Path.Combine(cueDir, fileName);
                LogMessage($"Found referenced file in CUE: {fileName}");
                referencedFiles.Add(filePath);
            }
        }
        catch (Exception ex)
        {
            LogMessage($"Error parsing CUE file {Path.GetFileName(cuePath)}: {ex.Message}");
            Task.Run(async () => await ReportBugAsync($"Error parsing CUE file: {Path.GetFileName(cuePath)}", ex));
        }

        return referencedFiles;
    }

    private async Task<bool> ConvertToChdAsync(string chdmanPath, string inputFile, string outputFile, int degreeOfParallelismForFiles)
    {
        try
        {
            var command = "createcd";
            var extension = Path.GetExtension(inputFile).ToLowerInvariant();
            switch (extension)
            {
                case ".img": command = "createhd"; break;
                case ".raw": command = "createraw"; break;
            }

            int numProcessorsForChdman;
            if (degreeOfParallelismForFiles > 1) // Multiple files processed in parallel by the app
            {
                numProcessorsForChdman = 1; // Each chdman uses 1 core
            }
            else // Sequential file processing by the app (degreeOfParallelismForFiles is 1)
            {
                numProcessorsForChdman = Environment.ProcessorCount; // Let this single chdman use all cores
            }

            if (numProcessorsForChdman <= 0)
            {
                numProcessorsForChdman = 1;
            }


            LogMessage($"Using CHDMAN command: {command} with {numProcessorsForChdman} processor(s) for this instance.");
            var arguments = $"{command} -i \"{inputFile}\" -o \"{outputFile}\" -f -np {numProcessorsForChdman}";

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = chdmanPath,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                },
                EnableRaisingEvents = true
            };

            var outputBuilder = new StringBuilder();
            var errorBuilder = new StringBuilder();
            process.OutputDataReceived += (_, args) =>
            {
                if (string.IsNullOrEmpty(args.Data)) return;

                outputBuilder.AppendLine(args.Data);
                if (args.Data.Contains("Compressing") && args.Data.Contains('%')) UpdateConversionProgress(args.Data);
            };
            process.ErrorDataReceived += (_, args) =>
            {
                if (string.IsNullOrEmpty(args.Data)) return;

                errorBuilder.AppendLine(args.Data);
                if ((args.Data.Contains("Compressing") && args.Data.Contains('%')) || args.Data.Contains("Compression complete"))
                {
                    if (args.Data.Contains("Compression complete")) LogMessage($"CHDMAN: {args.Data}");
                    else UpdateConversionProgress(args.Data);
                }
                else
                {
                    LogMessage($"[CHDMAN ERROR] {args.Data}");
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync(_cts.Token);

            LogMessage($"CHDMAN raw output for {Path.GetFileName(inputFile)}: {outputBuilder}");
            if (errorBuilder.Length > 0 && process.ExitCode != 0) LogMessage($"CHDMAN raw error for {Path.GetFileName(inputFile)}: {errorBuilder}");


            return process.ExitCode == 0;
        }
        catch (OperationCanceledException)
        {
            LogMessage($"Conversion cancelled for {Path.GetFileName(inputFile)}.");
            throw; // Re-throw to be handled by PerformBatchConversionAsync
        }
        catch (Exception ex)
        {
            LogMessage($"Error converting file {Path.GetFileName(inputFile)}: {ex.Message}");
            await ReportBugAsync($"Error converting file: {Path.GetFileName(inputFile)}", ex);
            return false;
        }
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
            fullReport.AppendLine();
            fullReport.AppendLine("=== Error Message ===");
            fullReport.AppendLine(message);
            fullReport.AppendLine();

            if (exception != null)
            {
                fullReport.AppendLine("=== Exception Details ===");
                AppendExceptionDetailsToReport(fullReport, exception);
            }

            if (LogViewer != null)
            {
                var logContent = await Dispatcher.InvokeAsync(() => LogViewer.Text);
                if (!string.IsNullOrEmpty(logContent))
                {
                    fullReport.AppendLine().AppendLine("=== Application Log ===").Append(logContent);
                }
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
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}StackTrace:");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}{ex.StackTrace}");
            if (ex.InnerException != null)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}Inner Exception:");
                ex = ex.InnerException;
                level++;
            }
            else
            {
                break;
            }
        }
    }

    private void UpdateConversionProgress(string progressLine)
    {
        try
        {
            var match = MyRegex().Match(progressLine);
            if (!match.Success) return;

            var percentageStr = match.Groups[1].Value.Replace(',', '.');
            if (!double.TryParse(percentageStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var percentage)) return;

            if (percentage > 100)
            {
                percentage /= 10;
            }

            var ratio = "unknown";
            var ratioMatch = MyRegex1().Match(progressLine);
            if (ratioMatch.Success)
            {
                ratio = ratioMatch.Groups[1].Value.Replace(',', '.') + "%";
            }

            LogMessage($"CHDMAN Converting: {percentage:F1}% complete (compression ratio: {ratio})");
        }
        catch (Exception ex)
        {
            LogMessage($"Error updating chdman progress: {ex.Message}");
        }
    }

    private async Task<bool> ProcessFileAsync(string chdmanPath, string inputFile, string outputFolder, bool deleteOriginal, int degreeOfParallelismForFiles)
    {
        try
        {
            var fileToProcess = inputFile;
            var isArchiveFile = false;
            var tempDir = string.Empty;
            var fileExtension = Path.GetExtension(inputFile).ToLowerInvariant();

            if (ArchiveExtensions.Contains(fileExtension))
            {
                LogMessage($"Processing archive: {Path.GetFileName(inputFile)}");
                var extractResult = await ExtractArchiveAsync(inputFile);
                if (extractResult.Success)
                {
                    fileToProcess = extractResult.FilePath;
                    tempDir = extractResult.TempDir;
                    isArchiveFile = true;
                    LogMessage($"Using extracted file: {Path.GetFileName(fileToProcess)} from archive {Path.GetFileName(inputFile)}");
                }
                else
                {
                    LogMessage($"Error extracting archive {Path.GetFileName(inputFile)}: {extractResult.ErrorMessage}");
                    return false;
                }
            }

            try
            {
                var outputFile = Path.Combine(outputFolder, Path.GetFileNameWithoutExtension(fileToProcess) + ".chd");
                var success = await ConvertToChdAsync(chdmanPath, fileToProcess, outputFile, degreeOfParallelismForFiles);

                if (!success || !deleteOriginal) return success;

                if (isArchiveFile) // Delete original archive
                {
                    TryDeleteFile(inputFile, $"original archive file: {Path.GetFileName(inputFile)}");
                }
                else // Delete original game files (cue+bin, gdi+tracks, etc.)
                {
                    await DeleteOriginalFilesAsync(fileToProcess);
                }

                return success;
            }
            finally
            {
                if (isArchiveFile && !string.IsNullOrEmpty(tempDir) && Directory.Exists(tempDir))
                {
                    TryDeleteDirectory(tempDir, "temporary extraction directory");
                }
            }
        }
        catch (OperationCanceledException)
        {
            LogMessage($"Processing cancelled for {Path.GetFileName(inputFile)}.");
            throw; // Re-throw
        }
        catch (Exception ex)
        {
            LogMessage($"Error processing file {Path.GetFileName(inputFile)}: {ex.Message}");
            await ReportBugAsync($"Error processing file: {Path.GetFileName(inputFile)}", ex);
            return false;
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

    private async Task<(bool Success, string FilePath, string TempDir, string ErrorMessage)> ExtractArchiveAsync(string archivePath)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var extension = Path.GetExtension(archivePath).ToLowerInvariant();
        var archiveFileName = Path.GetFileName(archivePath);

        try
        {
            Directory.CreateDirectory(tempDir);
            LogMessage($"Extracting {archiveFileName} to temporary directory: {tempDir}");

            if (extension == ".zip")
            {
                await Task.Run(() => ZipFile.ExtractToDirectory(archivePath, tempDir, true), _cts.Token);
            }
            else if (extension is ".7z" or ".rar")
            {
                if (!_isSevenZipAvailable)
                {
                    return (false, string.Empty, tempDir, $"{SevenZipExeName} not found. Cannot extract {extension} files.");
                }

                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = _sevenZipPath,
                        Arguments = $"x \"{archivePath}\" -o\"{tempDir}\" -y",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                var outputBuilder = new StringBuilder();
                var errorBuilder = new StringBuilder();
                process.OutputDataReceived += (_, args) =>
                {
                    if (args.Data != null) outputBuilder.AppendLine(args.Data);
                };
                process.ErrorDataReceived += (_, args) =>
                {
                    if (args.Data != null) errorBuilder.AppendLine(args.Data);
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                try
                {
                    await process.WaitForExitAsync(_cts.Token);
                }
                catch (OperationCanceledException)
                {
                    if (!process.HasExited)
                    {
                        process.Kill(true);
                    }

                    LogMessage($"Extraction of {archiveFileName} cancelled.");
                    throw;
                }

                if (process.ExitCode != 0)
                {
                    LogMessage($"Error extracting {archiveFileName} with {SevenZipExeName}. Exit code: {process.ExitCode}. Output: {outputBuilder}. Error: {errorBuilder}");
                    return (false, string.Empty, tempDir, $"7z.exe failed. Error: {errorBuilder.ToString().Trim()}");
                }

                LogMessage($"{SevenZipExeName} output for {archiveFileName}: {outputBuilder}");
            }
            else
            {
                return (false, string.Empty, tempDir, $"Unsupported archive type: {extension}");
            }

            var supportedFile = Directory.GetFiles(tempDir, "*.*", SearchOption.AllDirectories)
                .FirstOrDefault(static f => PrimaryTargetExtensionsInsideArchive.Contains(Path.GetExtension(f).ToLowerInvariant()));

            if (supportedFile != null)
            {
                return (true, supportedFile, tempDir, string.Empty);
            }

            return (false, string.Empty, tempDir, "No supported primary files found in archive.");
        }
        catch (OperationCanceledException)
        {
            TryDeleteDirectory(tempDir, $"cancelled extraction directory for {archiveFileName}");
            throw; // Re-throw
        }
        catch (Exception ex)
        {
            LogMessage($"Error extracting archive {archiveFileName}: {ex.Message}");
            await ReportBugAsync($"Error extracting archive: {archiveFileName}", ex);
            TryDeleteDirectory(tempDir, $"failed extraction directory for {archiveFileName}");
            return (false, string.Empty, tempDir, $"Exception during extraction: {ex.Message}");
        }
    }

    private async Task DeleteOriginalFilesAsync(string inputFile) // the inputFile is the primary file like .cue,
                                                                  // .gdi, .iso
    {
        try
        {
            var filesToDelete = new List<string> { inputFile }; // Always add the main input file

            var inputFileExtension = Path.GetExtension(inputFile).ToLowerInvariant();
            if (inputFileExtension == ".cue")
            {
                filesToDelete.AddRange(GetReferencedFilesFromCue(inputFile)
                    .Where(f => !f.Equals(inputFile, StringComparison.OrdinalIgnoreCase))); // Add referenced,
                                                                                            // avoid duplicates
            }
            else if (inputFileExtension == ".gdi")
            {
                filesToDelete.AddRange(GetReferencedFilesFromGdi(inputFile)
                    .Where(f => !f.Equals(inputFile, StringComparison.OrdinalIgnoreCase))); // Add referenced,
                                                                                            // avoid duplicates
            }

            filesToDelete = filesToDelete.Distinct(StringComparer.OrdinalIgnoreCase).ToList();


            foreach (var fileToDelete in filesToDelete)
            {
                TryDeleteFile(fileToDelete, "original file");
            }
        }
        catch (Exception ex)
        {
            LogMessage($"Failed to delete original file(s) for {Path.GetFileName(inputFile)}: {ex.Message}");
            await ReportBugAsync($"Failed to delete original file(s): {Path.GetFileName(inputFile)}", ex);
        }
    }

    private List<string> GetReferencedFilesFromGdi(string gdiPath)
    {
        var referencedFiles = new List<string>();
        var gdiDir = Path.GetDirectoryName(gdiPath) ?? string.Empty;
        try
        {
            LogMessage($"Parsing GDI file: {Path.GetFileName(gdiPath)}");
            var lines = File.ReadAllLines(gdiPath);
            for (var i = 1; i < lines.Length; i++) // Skip track count line
            {
                var trimmedLine = lines[i].Trim();
                if (string.IsNullOrWhiteSpace(trimmedLine)) continue;

                var parts = trimmedLine.Split(Separator, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 5) continue;

                var fileName = parts[4].Trim('"');
                var filePath = Path.Combine(gdiDir, fileName);
                LogMessage($"Found referenced file in GDI: {fileName}");
                referencedFiles.Add(filePath);
            }
        }
        catch (Exception ex)
        {
            LogMessage($"Error parsing GDI file {Path.GetFileName(gdiPath)}: {ex.Message}");
            Task.Run(async () => await ReportBugAsync($"Error parsing GDI file: {Path.GetFileName(gdiPath)}", ex));
        }

        return referencedFiles;
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

    [System.Text.RegularExpressions.GeneratedRegex(@"(\d+[\.,]\d+)%")]
    private static partial System.Text.RegularExpressions.Regex MyRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"ratio=(\d+[\.,]\d+)%")]
    private static partial System.Text.RegularExpressions.Regex MyRegex1();

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _bugReportService?.Dispose();
        GC.SuppressFinalize(this);
    }
}