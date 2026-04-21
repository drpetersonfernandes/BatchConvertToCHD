using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace BatchConvertToCHD.Services;

/// <summary>
/// Service responsible for sending bug reports to the BugReport API
/// </summary>
public class BugReportService : IDisposable
{
    private readonly HttpClient _httpClient = new();
    private readonly string _apiUrl;
    private readonly string _apiKey;
    private readonly string _applicationName;

    public BugReportService(string apiUrl, string apiKey, string applicationName)
    {
        _apiUrl = apiUrl;
        _apiKey = apiKey;
        _applicationName = applicationName;
        _httpClient.DefaultRequestHeaders.Add("X-API-KEY", _apiKey);
        _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
    }

    /// <summary>
    /// Sends a bug report to the API with full environment and exception details
    /// </summary>
    /// <param name="message">A summary of the error or bug report</param>
    /// <param name="ex">The exception object, if available</param>
    /// <returns>A task representing the asynchronous operation</returns>
    public async Task<bool> SendBugReportAsync(string message, Exception? ex = null)
    {
        try
        {
            // Build the formatted message with all details
            var formattedMessage = BuildFormattedReport(message, ex);

            // Get environment details
            var envDetails = GetEnvironmentDetails();

            // Get exception details
            var (exceptionType, exceptionMessage, exceptionSource, stackTrace) = GetExceptionDetails(ex);

            // Create the request payload matching the API's BugReportRequest model
            var requestPayload = new
            {
                message = formattedMessage,
                applicationName = _applicationName,
                version = envDetails.ApplicationVersion,
                userInfo = Environment.UserName,
                environment = "Production",
                stackTrace = stackTrace,
                osVersion = envDetails.OsVersion,
                architecture = envDetails.Architecture,
                bitness = envDetails.Bitness,
                windowsVersion = envDetails.WindowsVersion,
                processorCount = envDetails.ProcessorCount,
                baseDirectory = envDetails.BaseDirectory,
                tempPath = envDetails.TempPath
            };

            // Create JSON content
            var content = JsonContent.Create(requestPayload);

            // Send the request
            var response = await _httpClient.PostAsync(_apiUrl, content);

            // Return true if successful
            return response.IsSuccessStatusCode;
        }
        catch
        {
            // Silently fail if there's an exception to avoid infinite loops
            return false;
        }
    }

    /// <summary>
    /// Builds a formatted report string with all details for the message field
    /// </summary>
    private string BuildFormattedReport(string message, Exception? ex)
    {
        var sb = new StringBuilder();

        // === Environment Details ===
        sb.AppendLine("=== Environment Details ===");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Date: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Application Name: {_applicationName}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Application Version: {Assembly.GetExecutingAssembly().GetName().Version}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"OS Version: {Environment.OSVersion}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Architecture: {RuntimeInformation.ProcessArchitecture}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Bitness: {(Environment.Is64BitProcess ? "64-bit" : "32-bit")}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Windows Version: {Environment.OSVersion.Version}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Processor Count: {Environment.ProcessorCount}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Base Directory: {AppDomain.CurrentDomain.BaseDirectory}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Temp Path: {Path.GetTempPath()}");

        // === Error Details ===
        sb.AppendLine();
        sb.AppendLine("=== Error Details ===");
        sb.AppendLine(message);

        // === Exception Details ===
        if (ex != null)
        {
            sb.AppendLine();
            sb.AppendLine("=== Exception Details ===");
            AppendExceptionDetails(sb, ex);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Appends exception details to the StringBuilder
    /// </summary>
    private static void AppendExceptionDetails(StringBuilder sb, Exception exception, int level = 0)
    {
        while (true)
        {
            var indent = new string(' ', level * 2);

            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}Type: {exception.GetType().FullName}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}Message: {exception.Message}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}Source: {exception.Source ?? "N/A"}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}StackTrace:");
            if (!string.IsNullOrEmpty(exception.StackTrace))
            {
                var lines = exception.StackTrace.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}  {line}");
                }
            }
            else
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}  (No stack trace available)");
            }

            // If there's an inner exception, include it too
            if (exception.InnerException != null)
            {
                sb.AppendLine();
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}Inner Exception:");
                exception = exception.InnerException;
                level += 1;
                continue;
            }

            break;
        }
    }

    /// <summary>
    /// Gets exception details for structured API fields
    /// </summary>
    private static (string Type, string Message, string Source, string StackTrace) GetExceptionDetails(Exception? ex)
    {
        if (ex == null)
        {
            return ("N/A", "No exception provided", "N/A", "N/A");
        }

        var sb = new StringBuilder();
        var currentEx = ex;
        var depth = 0;

        while (currentEx != null && depth < 5) // Limit to 5 nested exceptions
        {
            if (depth > 0)
            {
                sb.AppendLine();
                sb.AppendLine("--- Inner Exception ---");
            }

            sb.AppendLine($"Type: {currentEx.GetType().FullName}");
            sb.AppendLine($"Message: {currentEx.Message}");
            sb.AppendLine($"Source: {currentEx.Source ?? "N/A"}");
            sb.AppendLine("StackTrace:");
            if (!string.IsNullOrEmpty(currentEx.StackTrace))
            {
                sb.AppendLine(currentEx.StackTrace);
            }
            else
            {
                sb.AppendLine("(No stack trace available)");
            }

            currentEx = currentEx.InnerException;
            depth++;
        }

        return (
            ex.GetType().FullName ?? "Unknown",
            ex.Message,
            ex.Source ?? "N/A",
            sb.ToString()
        );
    }

    /// <summary>
    /// Gets environment details for structured API fields
    /// </summary>
    private static (string OsVersion, string Architecture, string Bitness, string WindowsVersion,
                    int ProcessorCount, string BaseDirectory, string TempPath, string ApplicationVersion) GetEnvironmentDetails()
    {
        return (
            Environment.OSVersion.ToString(),
            RuntimeInformation.ProcessArchitecture.ToString(),
            Environment.Is64BitProcess ? "64-bit" : "32-bit",
            Environment.OSVersion.Version.ToString(),
            Environment.ProcessorCount,
            AppDomain.CurrentDomain.BaseDirectory,
            Path.GetTempPath(),
            Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "Unknown"
        );
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        GC.SuppressFinalize(this);
    }
}
