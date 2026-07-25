using Serilog.Core;
using Serilog.Events;

namespace BatchConvertToCHD.Services;

/// <summary>
/// A Serilog log event sink that forwards warning-level and above log events to the
/// <see cref="BugReportService"/> for bug report submission. Events below
/// <see cref="LogEventLevel.Warning"/> are silently ignored. Messages matching
/// known informational patterns (rate-limiting, disk-space warnings, archive
/// file-type info) are also excluded.
/// Uses an interlocked flag to prevent concurrent API flood when many warnings fire rapidly.
/// </summary>
internal class BugReportApiSink : ILogEventSink
{
    private readonly BugReportService _bugReportService;
    private static int _isSending;

    private static readonly string[] ExcludedMessagePatterns =
    [
        "Failed to record usage statistics",
        "Temp drive (",
        "Output drive (",
        "drive has ",
        "has ",
        "drive (",
        "drive has",
        "input files total",
        "CHD files total",
        "You may run out of disk space",
        "Temporary files are created during conversion",
        "CHD compression usually reduces",
        "Extracted files are typically larger",
        "No supported primary files found in archive",
        "disk space",
        "disk full"
    ];

    private static bool IsExcluded(string message)
    {
        foreach (var pattern in ExcludedMessagePatterns)
        {
            if (message.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="BugReportApiSink"/> class.
    /// </summary>
    /// <param name="bugReportService">The bug report service to forward warning events to.</param>
    internal BugReportApiSink(BugReportService bugReportService)
    {
        _bugReportService = bugReportService;
    }

    /// <summary>
    /// Emits the provided log event to the sink. Only events at or above
    /// <see cref="LogEventLevel.Warning"/> are forwarded to the bug report API.
    /// Messages matching informational patterns (rate-limiting, disk-space, etc.) are excluded.
    /// </summary>
    /// <param name="logEvent">The log event to emit.</param>
    public void Emit(LogEvent logEvent)
    {
        if (logEvent.Level < LogEventLevel.Warning)
            return;

        var message = logEvent.RenderMessage();

        if (IsExcluded(message))
            return;

        var ex = logEvent.Exception;

        if (Interlocked.CompareExchange(ref _isSending, 1, 0) == 0)
        {
            _ = _bugReportService.SendBugReportAsync(message, ex)
                .ContinueWith(static _ =>
                {
                    Interlocked.Exchange(ref _isSending, 0);
                }, TaskContinuationOptions.ExecuteSynchronously);
        }
    }
}
