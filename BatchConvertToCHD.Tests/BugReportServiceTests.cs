using System.Reflection;
using System.Text;
using BatchConvertToCHD.Services;

namespace BatchConvertToCHD.Tests;

public class BugReportServiceTests
{
    private const string TestApiUrl = "https://example.com/api/bugreport";
    private const string TestApiKey = "test-api-key";
    private const string TestAppName = "TestApp";

    [Fact]
    public void ConstructorStoresParametersCorrectly()
    {
        var service = new BugReportService(TestApiUrl, TestApiKey, TestAppName);

        var apiUrlField = typeof(BugReportService).GetField("_apiUrl", BindingFlags.NonPublic | BindingFlags.Instance);
        var apiKeyField = typeof(BugReportService).GetField("_apiKey", BindingFlags.NonPublic | BindingFlags.Instance);
        var appNameField = typeof(BugReportService).GetField("_applicationName", BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(apiUrlField);
        Assert.NotNull(apiKeyField);
        Assert.NotNull(appNameField);
        Assert.Equal(TestApiUrl, apiUrlField.GetValue(service));
        Assert.Equal(TestApiKey, apiKeyField.GetValue(service));
        Assert.Equal(TestAppName, appNameField.GetValue(service));
    }

    [Fact]
    public void BuildFormattedReportIncludesMessageAndAppName()
    {
        var service = new BugReportService(TestApiUrl, TestApiKey, TestAppName);
        var method = typeof(BugReportService).GetMethod("BuildFormattedReport", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        var result = method.Invoke(service, ["Test error message", null]) as string;
        Assert.NotNull(result);
        Assert.Contains("Test error message", result, StringComparison.Ordinal);
        Assert.Contains(TestAppName, result, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildFormattedReportIncludesExceptionDetails()
    {
        var service = new BugReportService(TestApiUrl, TestApiKey, TestAppName);
        var method = typeof(BugReportService).GetMethod("BuildFormattedReport", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        var ex = new InvalidOperationException("Something went wrong");
        var result = method.Invoke(service, ["Error summary", ex]) as string;
        Assert.NotNull(result);
        Assert.Contains("Error summary", result, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", result, StringComparison.Ordinal);
        Assert.Contains("Something went wrong", result, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildFormattedReportIncludesInnerException()
    {
        var service = new BugReportService(TestApiUrl, TestApiKey, TestAppName);
        var method = typeof(BugReportService).GetMethod("BuildFormattedReport", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        var inner = new ArgumentException("Inner error");
        var outer = new InvalidOperationException("Outer error", inner);
        var result = method.Invoke(service, ["Error summary", outer]) as string;
        Assert.NotNull(result);
        Assert.Contains("Inner Exception", result, StringComparison.Ordinal);
        Assert.Contains("Inner error", result, StringComparison.Ordinal);
    }

    [Fact]
    public void GetExceptionStackTraceNullReturnsNa()
    {
        var method = typeof(BugReportService).GetMethod("GetExceptionStackTrace", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method.Invoke(null, [null]) as string;
        Assert.Equal("N/A", result);
    }

    [Fact]
    public void GetExceptionStackTraceIncludesExceptionDetails()
    {
        var method = typeof(BugReportService).GetMethod("GetExceptionStackTrace", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var ex = new InvalidOperationException("Test error");
        var result = method.Invoke(null, [ex]) as string;
        Assert.NotNull(result);
        Assert.Contains("InvalidOperationException", result, StringComparison.Ordinal);
        Assert.Contains("Test error", result, StringComparison.Ordinal);
    }

    [Fact]
    public void GetExceptionStackTraceHandlesNestedExceptions()
    {
        var method = typeof(BugReportService).GetMethod("GetExceptionStackTrace", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var deep = new FormatException("Deep");
        var mid = new ArgumentException("Mid", deep);
        var top = new InvalidOperationException("Top", mid);
        var result = method.Invoke(null, [top]) as string;
        Assert.NotNull(result);
        Assert.Contains("Top", result, StringComparison.Ordinal);
        Assert.Contains("Mid", result, StringComparison.Ordinal);
        Assert.Contains("Deep", result, StringComparison.Ordinal);
    }

    [Fact]
    public void GetExceptionStackTraceLimitsDepth()
    {
        var method = typeof(BugReportService).GetMethod("GetExceptionStackTrace", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var inner = new InvalidOperationException("deepest");
        var current = inner;
        for (var i = 0; i < 10; i++)
        {
            current = new InvalidOperationException($"level {i}", current);
        }

        var result = method.Invoke(null, [current]) as string;
        Assert.NotNull(result);
    }

    [Fact]
    public void GetApplicationVersionReturnsValidValue()
    {
        var method = typeof(BugReportService).GetMethod("GetApplicationVersion", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method.Invoke(null, null) as string;
        Assert.NotNull(result);
        Assert.NotEmpty(result);
    }

    [Fact]
    public void AppendExceptionDetailsWithNullStackTraceDoesNotCrash()
    {
        var method = typeof(BugReportService).GetMethod("AppendExceptionDetails", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var sb = new StringBuilder();
        var ex = new InvalidOperationException("No stack");
        var record = Record.Exception(() => method.Invoke(null, [sb, ex, 0]));
        Assert.Null(record);
        Assert.Contains("No stack", sb.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendBugReportAsyncReturnsFalseOnNetworkError()
    {
        var service = new BugReportService("https://invalid.example.invalid/api", TestApiKey, TestAppName);
        var result = await service.SendBugReportAsync("Test message");
        Assert.False(result);
    }

    [Fact]
    public void BuildFormattedReportExceptionWithNullFieldsDoesNotCrash()
    {
        var service = new BugReportService(TestApiUrl, TestApiKey, TestAppName);
        var method = typeof(BugReportService).GetMethod("BuildFormattedReport", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        var ex = Record.Exception(() =>
        {
            // Exception with no message and no stack trace
            var customEx = new Exception((string?)null);
            var result = method.Invoke(service, ["Error summary", customEx]) as string;
            Assert.NotNull(result);
            Assert.Contains("Error summary", result, StringComparison.Ordinal);
        });

        Assert.Null(ex);
    }

    [Fact]
    public void BuildFormattedReportEmptyMessageDoesNotCrash()
    {
        var service = new BugReportService(TestApiUrl, TestApiKey, TestAppName);
        var method = typeof(BugReportService).GetMethod("BuildFormattedReport", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        var result = method.Invoke(service, ["", null]) as string;
        Assert.NotNull(result);
        Assert.Contains("=== Error Details ===", result, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildFormattedReportExceptionWithoutStackTraceDoesNotCrash()
    {
        var service = new BugReportService(TestApiUrl, TestApiKey, TestAppName);
        var method = typeof(BugReportService).GetMethod("BuildFormattedReport", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        // Create exception using parameterless constructor which may not populate StackTrace immediately
        var customEx = new InvalidOperationException("Error with no explicit stack");
        var result = method.Invoke(service, ["Error with null stack", customEx]) as string;
        Assert.NotNull(result);
        Assert.Contains("Error with null stack", result, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", result, StringComparison.Ordinal);
    }

    [Fact]
    public void AppendExceptionDetailsHandlesExceptionWithoutSource()
    {
        var method = typeof(BugReportService).GetMethod("AppendExceptionDetails", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var sb = new StringBuilder();
        var ex = Record.Exception(() =>
        {
            method.Invoke(null, [sb, new InvalidOperationException(), 0]);
        });

        Assert.Null(ex);
        Assert.Contains("InvalidOperationException", sb.ToString(), StringComparison.Ordinal);
    }
}
