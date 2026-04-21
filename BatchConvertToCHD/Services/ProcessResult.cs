namespace BatchConvertToCHD.Services;

/// <summary>
/// Represents the result of an external process execution.
/// </summary>
public class ProcessResult
{
    /// <summary>
    /// Gets or sets a value indicating whether the process completed successfully.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Gets or sets the exit code returned by the process.
    /// </summary>
    public int ExitCode { get; set; }

    /// <summary>
    /// Gets or sets the standard output captured from the process.
    /// </summary>
    public string StandardOutput { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the standard error output captured from the process.
    /// </summary>
    public string StandardError { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the error message if the process execution failed.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Gets or sets the duration of the process execution.
    /// </summary>
    public TimeSpan Duration { get; set; }
}
