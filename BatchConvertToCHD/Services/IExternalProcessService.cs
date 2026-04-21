namespace BatchConvertToCHD.Services;

/// <summary>
/// Defines a contract for executing external processes with configurable options.
/// </summary>
public interface IExternalProcessService
{
    /// <summary>
    /// Executes an external process asynchronously with the specified executable, arguments, and options.
    /// </summary>
    /// <param name="executable">The path to the executable to run.</param>
    /// <param name="arguments">The arguments to pass to the executable.</param>
    /// <param name="options">Configuration options for the process execution.</param>
    /// <returns>A task that represents the asynchronous operation, containing the process result.</returns>
    Task<ProcessResult> ExecuteAsync(string executable, string arguments, ProcessOptions options);
}
