using Serilog.Core;
using Serilog.Events;

namespace BatchConvertToCHD.Services;

/// <summary>
/// A Serilog log event sink that forwards warning-level and above log events to the
/// <see cref="BugReportService"/> for bug report submission. Events below
/// <see cref="LogEventLevel.Warning"/> are silently ignored. Messages matching
/// known informational patterns (rate-limiting, disk-space warnings, missing
/// user files, archive corruption, missing dependencies) are also excluded.
/// Uses an interlocked flag to prevent concurrent API flood when many warnings fire rapidly.
/// </summary>
internal class BugReportApiSink : ILogEventSink
{
    private readonly BugReportService _bugReportService;
    private static int _isSending;

    private static readonly string[] ExcludedMessagePatterns =
    [
        // Stats / rate-limiting
        "Failed to record usage statistics",

        // Disk space warnings (informational, not bugs)
        "Temp drive (",
        "Output drive (",
        "drive has ",
        "drive (",
        "input files total",
        "CHD files total",
        "You may run out of disk space",
        "Temporary files are created during conversion",
        "CHD compression usually reduces",
        "Extracted files are typically larger",
        "disk space",
        "disk full",

        // Archive info (not code bugs)
        "No supported primary files found in archive",

        // chdman.exe missing — user environment issue, not a code bug
        "chdman.exe not found",
        "CRITICAL ERROR: The following required component is missing",

        // User file issues — corrupt, truncated, or incomplete ROMs
        "referenced files are missing",
        "is not divisible by sector size",
        "could not validate referenced files",
        "The file or directory is corrupted and unreadable",
        "Retry via temp failed",
        "archive file may be corrupted",
        "archive is invalid or corrupt",
        "archive file appears to be incomplete",
        "archive file may be corrupted or in an unsupported format",
        "archive file may be corrupted or unsupported",
        "Archive is encrypted",
        "compression method that is not supported",

        // CCDSharp — missing .img alongside .ccd (user issue)
        "CCDSharp: Conversion error",

        // File not found during processing — user moved/deleted file
        "File not found, skipping:"
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
    /// Messages matching informational patterns (rate-limiting, disk-space,
    /// missing user files, missing dependencies, etc.) are excluded.
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
