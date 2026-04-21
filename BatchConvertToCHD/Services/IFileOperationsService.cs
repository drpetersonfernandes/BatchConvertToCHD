namespace BatchConvertToCHD.Services;

/// <summary>
/// Defines a contract for file operations with retry logic for improved reliability.
/// </summary>
public interface IFileOperationsService
{
    /// <summary>
    /// Copies a file from source to destination with retry logic.
    /// </summary>
    /// <param name="source">The source file path.</param>
    /// <param name="dest">The destination file path.</param>
    /// <param name="token">Cancellation token to cancel the operation.</param>
    /// <returns>A task that represents the asynchronous operation, returning true if the copy succeeded.</returns>
    Task<bool> CopyWithRetryAsync(string source, string dest, CancellationToken token);

    /// <summary>
    /// Deletes a file with retry logic to handle transient locks.
    /// </summary>
    /// <param name="path">The file path to delete.</param>
    /// <param name="token">Cancellation token to cancel the operation.</param>
    /// <returns>A task that represents the asynchronous operation, returning true if the deletion succeeded.</returns>
    Task<bool> DeleteWithRetryAsync(string path, CancellationToken token);
}
