namespace BatchConvertToCHD.Services;

/// <summary>
/// Represents configuration options for executing an external process.
/// </summary>
public class ProcessOptions
{
    /// <summary>
    /// Gets or sets the working directory for the process.
    /// If null or empty, the current directory is used.
    /// </summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to capture standard output.
    /// </summary>
    public bool CaptureStandardOutput { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to capture standard error.
    /// </summary>
    public bool CaptureStandardError { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to redirect standard input.
    /// </summary>
    public bool RedirectStandardInput { get; set; }

    /// <summary>
    /// Gets or sets environment variables to set for the process.
    /// </summary>
    public Dictionary<string, string>? EnvironmentVariables { get; set; }

    /// <summary>
    /// Gets or sets the timeout for the process execution.
    /// If null, no timeout is applied.
    /// </summary>
    public TimeSpan? Timeout { get; set; }

    /// <summary>
    /// Gets or sets the cancellation token for the process execution.
    /// </summary>
    public CancellationToken CancellationToken { get; set; }

    /// <summary>
    /// Gets or sets a callback that is invoked when standard output data is received.
    /// </summary>
    public Action<string>? OnStandardOutput { get; set; }

    /// <summary>
    /// Gets or sets a callback that is invoked when standard error data is received.
    /// </summary>
    public Action<string>? OnStandardError { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to create a window for the process.
    /// </summary>
    public bool CreateNoWindow { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to use the operating system shell to start the process.
    /// </summary>
    public bool UseShellExecute { get; set; }
}
