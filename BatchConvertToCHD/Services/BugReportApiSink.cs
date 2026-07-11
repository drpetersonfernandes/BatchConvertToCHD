using Serilog.Core;
using Serilog.Events;

namespace BatchConvertToCHD.Services;

public class BugReportApiSink : ILogEventSink
{
    private readonly BugReportService _bugReportService;

    public BugReportApiSink(BugReportService bugReportService)
    {
        _bugReportService = bugReportService;
    }

    public void Emit(LogEvent logEvent)
    {
        if (logEvent.Level < LogEventLevel.Warning)
            return;

        var message = logEvent.RenderMessage();
        var ex = logEvent.Exception;

        _ = _bugReportService.SendBugReportAsync(message, ex);
    }
}
