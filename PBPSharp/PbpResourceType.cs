namespace PBPSharp;

/// <summary>
/// Types of resources embedded in a PBP file.
/// </summary>
public enum PbpResourceType
{
    /// <summary>
    /// PARAM.SFO metadata file.
    /// </summary>
    Sfo,

    /// <summary>
    /// ICON0.PNG - Game icon displayed on the PSP XMB.
    /// </summary>
    Icon0,

    /// <summary>
    /// ICON1.PMF or ICON1.PNG - Animated or alternate icon.
    /// </summary>
    Icon1,

    /// <summary>
    /// PIC0.PNG - Background image.
    /// </summary>
    Pic0,

    /// <summary>
    /// PIC1.PNG - Background image (widescreen).
    /// </summary>
    Pic1,

    /// <summary>
    /// SND0.AT3 - Background audio.
    /// </summary>
    Snd0,

    /// <summary>
    /// DATA.PSP - PSP executable data.
    /// </summary>
    DataPsp,

    /// <summary>
    /// DATA.PSAR - Contains the compressed ISO image(s).
    /// </summary>
    DataPsar
}
