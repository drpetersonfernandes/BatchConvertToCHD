namespace BatchConvertToCHD.Models;

/// <summary>
/// Represents the result of a PBP (PlayStation Portable) file extraction operation.
/// </summary>
internal sealed class PbpExtractionResult
{
    /// <summary>
    /// Gets or sets a value indicating whether the extraction was successful.
    /// </summary>
    internal bool Success { get; set; }

    /// <summary>
    /// Gets the list of extracted CUE file paths.
    /// </summary>
    internal List<string> CueFilePaths { get; init; } = new();

    /// <summary>
    /// Gets or sets the output folder path where files were extracted.
    /// </summary>
    internal string? OutputFolder { get; set; }
}
