namespace BatchConvertToCHD.Utilities;

/// <summary>
/// Centralized constants for file extensions used throughout the application.
/// </summary>
internal static class FileExtensions
{
    /// <summary>
    /// String comparer for ordinal case-insensitive extension comparisons.
    /// </summary>
    private static readonly StringComparer ExtensionComparer = StringComparer.OrdinalIgnoreCase;

    // Disc image formats
    internal const string Cue = ".cue";
    internal const string Iso = ".iso";
    internal const string Img = ".img";
    internal const string Gdi = ".gdi";
    internal const string Toc = ".toc";
    internal const string Raw = ".raw";
    internal const string Ccd = ".ccd";
    internal const string Sub = ".sub";

    // Archive formats
    internal const string Zip = ".zip";
    internal const string SevenZip = ".7z";
    internal const string Rar = ".rar";

    // Compressed disc image formats
    internal const string Cso = ".cso";
    internal const string Pbp = ".pbp";

    // Output format
    internal const string Chd = ".chd";

    /// <summary>
    /// All supported input extensions for conversion.
    /// </summary>
    internal static readonly string[] AllSupportedInputExtensionsForConversion =
    [
        Cue, Iso, Img, Gdi, Toc, Raw, Zip, SevenZip, Rar, Cso, Pbp
    ];

    /// <summary>
    /// HashSet of all supported input extensions for efficient case-insensitive lookups.
    /// </summary>
    internal static readonly HashSet<string> AllSupportedInputExtensionsForConversionSet =
        new(AllSupportedInputExtensionsForConversion, ExtensionComparer);

    /// <summary>
    /// Archive file extensions.
    /// </summary>
    internal static readonly string[] ArchiveExtensions =
    [
        Zip, SevenZip, Rar
    ];

    /// <summary>
    /// HashSet of archive extensions for efficient case-insensitive lookups.
    /// </summary>
    internal static readonly HashSet<string> ArchiveExtensionsSet =
        new(ArchiveExtensions, ExtensionComparer);

    /// <summary>
    /// Primary target extensions for extraction from archives.
    /// </summary>
    internal static readonly string[] PrimaryTargetExtensions =
    [
        Cue, Iso, Img, Gdi, Toc, Raw
    ];

    /// <summary>
    /// HashSet of primary target extensions for efficient case-insensitive lookups.
    /// </summary>
    internal static readonly HashSet<string> PrimaryTargetExtensionsSet =
        new(PrimaryTargetExtensions, ExtensionComparer);
}
