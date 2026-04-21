namespace BatchConvertToCHD.Models;

/// <summary>
/// Represents the result of a PBP (PlayStation Portable) file extraction operation.
/// </summary>
public class PbpExtractionResult
{
    /// <summary>
    /// Gets or sets a value indicating whether the extraction was successful.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Gets or sets the list of extracted CUE file paths.
    /// </summary>
    public List<string> CueFilePaths { get; set; } = new();

    /// <summary>
    /// Gets or sets the output folder path where files were extracted.
    /// </summary>
    public string? OutputFolder { get; set; }
}
