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
public class BugReportService(string apiUrl, string apiKey, string applicationName) : IDisposable
{
    private readonly HttpClient _httpClient = new();
    private readonly string _apiUrl = apiUrl;
    private readonly string _apiKey = apiKey;
    private readonly string _applicationName = applicationName;

    /// <summary>
    /// Sends a bug report to the API
    /// </summary>
    /// <param name="message">A summary of the error or bug report</param>
    /// <param name="ex">The exception object, if available</param>
    /// <returns>A task representing the asynchronous operation</returns>
    public async Task<bool> SendBugReportAsync(string message, Exception? ex = null)
    {
        // Build the full report message
        var fullReport = BuildExceptionReport(message, ex);

        try
        {
            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.Add("X-API-KEY", _apiKey);

            // Create the request payload
            var content = JsonContent.Create(new
            {
                message = fullReport,
                applicationName = _applicationName
            });

            // Send the request
            var response = await _httpClient.PostAsync(_apiUrl, content);

            // Return true if successful
            return response.IsSuccessStatusCode;
        }
        catch
        {
            // Silently fail if there's an exception
            return false;
        }
    }

    private static string BuildExceptionReport(string message, Exception? exception)
    {
        var sb = new StringBuilder();
        sb.AppendLine(GetEnvironmentDetailsReport());
        sb.AppendLine();
        sb.AppendLine("=== Error Details ===");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Custom Message: {message}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Date and Time: {DateTime.Now}");

        // Add exception details if provided
        if (exception != null)
        {
            sb.AppendLine("Exception Details:");
            AppendExceptionDetails(sb, exception);
        }

        return sb.ToString();
    }

    private static void AppendExceptionDetails(StringBuilder sb, Exception exception, int level = 0)
    {
        while (true)
        {
            var indent = new string(' ', level * 2);

            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}Type: {exception.GetType().FullName}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}Message: {exception.Message}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}Source: {exception.Source}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}StackTrace:");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}{exception.StackTrace}");

            // If there's an inner exception, include it too
            if (exception.InnerException != null)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}Inner Exception:");
                exception = exception.InnerException;
                level += 1;
                continue;
            }

            break;
        }
    }

    private static string GetEnvironmentDetailsReport()
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== Environment Details ===");
            sb.AppendLine(CultureInfo.InvariantCulture, $"OS Version: {Environment.OSVersion}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"Windows Version: {Environment.OSVersion.Version}");
            sb.AppendLine(CultureInfo.InvariantCulture, $".NET Version: {Environment.Version}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"Process Architecture: {RuntimeInformation.ProcessArchitecture}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"Processor Count: {Environment.ProcessorCount}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"Application Version: {Assembly.GetExecutingAssembly().GetName().Version}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"Base Directory: {AppDomain.CurrentDomain.BaseDirectory}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"Temp Path: {Path.GetTempPath()}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"User: {Environment.UserName}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"CHDMAN Available: {File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "chdman.exe"))}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"MAXCSO Available: {File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "maxcso.exe"))}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"7-Zip Available: {App.IsSevenZipAvailable}");
            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"Failed to get environment details: {ex.Message}";
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        GC.SuppressFinalize(this);
    }
}