using Serilog.Core;
using Serilog.Events;

namespace BatchConvertToCHD.Services;

/// <summary>
/// A Serilog log event sink that forwards warning-level and above log events to the
/// <see cref="BugReportService"/> for bug report submission. Events below
/// <see cref="LogEventLevel.Warning"/> are silently ignored.
/// </summary>
public class BugReportApiSink : ILogEventSink
{
    private readonly BugReportService _bugReportService;

    /// <summary>
    /// Initializes a new instance of the <see cref="BugReportApiSink"/> class.
    /// </summary>
    /// <param name="bugReportService">The bug report service to forward warning events to.</param>
    public BugReportApiSink(BugReportService bugReportService)
    {
        _bugReportService = bugReportService;
    }

    /// <summary>
    /// Emits the provided log event to the sink. Only events at or above
    /// <see cref="LogEventLevel.Warning"/> are forwarded to the bug report API.
    /// </summary>
    /// <param name="logEvent">The log event to emit.</param>
    public void Emit(LogEvent logEvent)
    {
        if (logEvent.Level < LogEventLevel.Warning)
            return;

        var message = logEvent.RenderMessage();
        var ex = logEvent.Exception;

        _ = _bugReportService.SendBugReportAsync(message, ex);
    }
}
