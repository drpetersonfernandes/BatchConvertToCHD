using System.Buffers;
using System.Text;

namespace PBPSharp;

/// <summary>
/// Provides functionality to open and read PBP (EBOOT.PBP) files.
/// Supports single-disc and multi-disc PlayStation PBP files.
/// </summary>
public sealed class PbpFile : IDisposable
{
    private Stream _stream;
    private readonly bool _ownsStream;
    private bool _disposed;

    private static readonly byte[] Psisoimg = "PSISOIMG0000"u8.ToArray();
    private static readonly byte[] Pstitleimg = "PSTITLEIMG000000"u8.ToArray();

    /// <summary>
    /// The parsed PBP header containing resource offsets.
    /// </summary>
    public PbpHeader Header { get; }

    /// <summary>
    /// The SFO (PARAM.SFO) metadata parsed from the PBP.
    /// </summary>
    public SfoData SfoData { get; }

    /// <summary>
    /// The list of disc entries found in the PBP.
    /// </summary>
    public IReadOnlyList<PbpDiscInfo> Discs { get; }

    /// <summary>
    /// Whether this is a multi-disc PBP.
    /// </summary>
    public bool IsMultiDisc => Discs.Count > 1;

    /// <summary>
    /// The game title from SFO metadata.
    /// </summary>
    public string? Title => SfoData.GetString(SfoData.Keys.Title);

    /// <summary>
    /// The disc ID from SFO metadata (first disc).
    /// </summary>
    public string? DiscId => SfoData.GetString(SfoData.Keys.DiscId);

    /// <summary>
    /// The game category (e.g., "ME" for PS1 EBOOT).
    /// </summary>
    public string? Category => SfoData.GetString(SfoData.Keys.Category);

    private PbpFile(Stream stream, bool ownsStream, PbpHeader header, SfoData sfoData, IReadOnlyList<PbpDiscInfo> discs)
    {
        _stream = stream;
        _ownsStream = ownsStream;
        Header = header;
        SfoData = sfoData;
        Discs = discs;
    }

    /// <summary>
    /// Opens a PBP file from the specified file path.
    /// </summary>
    /// <param name="path">The full path to the PBP file.</param>
    /// <param name="pbp">When this method returns, contains the opened <see cref="PbpFile"/> instance if successful; otherwise, null.</param>
    /// <returns>A <see cref="PbpError"/> indicating the result of the operation.</returns>
    public static PbpError Open(string path, out PbpFile? pbp)
    {
        pbp = null;

        if (!File.Exists(path))
            return PbpError.FileNotFound;

        try
        {
            var stream = File.OpenRead(path);
            var error = Open(stream, ownsStream: true, out pbp);
            if (error != PbpError.None)
                stream.Dispose();
            return error;
        }
        catch (IOException)
        {
            return PbpError.IoError;
        }
    }

    /// <summary>
    /// Opens a PBP from an existing stream.
    /// </summary>
    /// <param name="stream">The stream containing PBP data. Must be seekable and readable.</param>
    /// <param name="ownsStream">Whether this instance should dispose the stream when disposed.</param>
    /// <param name="pbp">When this method returns, contains the opened <see cref="PbpFile"/> instance if successful; otherwise, null.</param>
    /// <returns>A <see cref="PbpError"/> indicating the result of the operation.</returns>
    public static PbpError Open(Stream stream, bool ownsStream, out PbpFile? pbp)
    {
        pbp = null;

        if (stream is not { CanRead: true } || !stream.CanSeek)
            return PbpError.IoError;

        try
        {
            stream.Seek(0, SeekOrigin.Begin);

            var headerError = ReadHeader(stream, out var header);
            if (headerError != PbpError.None)
                return headerError;

            var sfoError = ReadSfo(stream, header, out var sfoData);
            if (sfoError != PbpError.None)
                return sfoError;

            var discError = ReadDiscs(stream, header, out var discs);
            if (discError != PbpError.None)
                return discError;

            pbp = new PbpFile(stream, ownsStream, header, sfoData, discs);
            return PbpError.None;
        }
        catch (IOException)
        {
            return PbpError.IoError;
        }
        catch (InvalidDataException)
        {
            return PbpError.CorruptFile;
        }
        catch (Exception)
        {
            return PbpError.CorruptFile;
        }
    }

    private static PbpError ReadHeader(Stream stream, out PbpHeader header)
    {
        header = default;

        Span<byte> headerBytes = stackalloc byte[PbpHeader.HeaderSize];
        if (stream.Read(headerBytes) != PbpHeader.HeaderSize)
            return PbpError.InvalidHeader;

        var magic = BitConverter.ToUInt32(headerBytes[..4]);
        if (magic != PbpHeader.MagicValue)
            return PbpError.InvalidHeader;

        var version = BitConverter.ToUInt32(headerBytes[4..8]);
        var sfoOffset = BitConverter.ToInt32(headerBytes[8..12]);
        var icon0Offset = BitConverter.ToInt32(headerBytes[12..16]);
        var icon1Offset = BitConverter.ToInt32(headerBytes[16..20]);
        var pic0Offset = BitConverter.ToInt32(headerBytes[20..24]);
        var pic1Offset = BitConverter.ToInt32(headerBytes[24..28]);
        var snd0Offset = BitConverter.ToInt32(headerBytes[28..32]);
        var dataPspOffset = BitConverter.ToInt32(headerBytes[32..36]);
        var dataPsarOffset = BitConverter.ToInt32(headerBytes[36..40]);

        header = new PbpHeader(version, sfoOffset, icon0Offset, icon1Offset, pic0Offset, pic1Offset, snd0Offset, dataPspOffset, dataPsarOffset);
        return PbpError.None;
    }

    private static PbpError ReadSfo(Stream stream, PbpHeader header, out SfoData sfoData)
    {
        sfoData = new SfoData();

        stream.Seek(header.SfoOffset, SeekOrigin.Begin);
        var sfoBuffer = new byte[4];

        sfoData.Magic = ReadUInt32(stream, sfoBuffer);
        sfoData.Version = ReadUInt32(stream, sfoBuffer);
        sfoData.KeyTableOffset = ReadUInt32(stream, sfoBuffer);
        sfoData.DataTableOffset = ReadUInt32(stream, sfoBuffer);
        var entryCount = ReadUInt32(stream, sfoBuffer);

        var entries = new List<SfoEntry>();
        for (var i = 0; i < entryCount; i++)
        {
            var dirBuffer = new byte[16];
            stream.Seek(header.SfoOffset + 16 + i * 16, SeekOrigin.Begin);
            stream.ReadExactly(dirBuffer, 0, 16);

            // Layout: KeyOffset(2) + Format(2) + Length(4) + MaxLength(4) + DataOffset(4)
            var keyOffset = BitConverter.ToUInt16(dirBuffer, 0);
            var entry = new SfoEntry
            {
                Format = BitConverter.ToUInt16(dirBuffer, 2),
                Length = BitConverter.ToUInt32(dirBuffer, 4),
                MaxLength = BitConverter.ToUInt32(dirBuffer, 8)
            };

            var dataOffset = BitConverter.ToUInt32(dirBuffer, 12);

            stream.Seek(header.SfoOffset + sfoData.KeyTableOffset + keyOffset, SeekOrigin.Begin);
            entry.Key = ReadNullTerminatedString(stream, 128);

            stream.Seek(header.SfoOffset + sfoData.DataTableOffset + dataOffset, SeekOrigin.Begin);
            switch (entry.Format)
            {
                case 0x0204:
                    entry.Value = ReadNullTerminatedString(stream, (int)entry.Length);
                    break;
                case 0x0404:
                    entry.Value = ReadUInt32(stream, new byte[4]);
                    break;
            }

            entries.Add(entry);
        }

        sfoData.Entries = entries;
        return PbpError.None;
    }

    private static PbpError ReadDiscs(Stream stream, PbpHeader header, out List<PbpDiscInfo> discs)
    {
        discs = [];

        stream.Seek(header.DataPsarOffset, SeekOrigin.Begin);
        var psarHeaderBuffer = new byte[16];
        stream.ReadExactly(psarHeaderBuffer, 0, 16);
        var psarHeader = Encoding.ASCII.GetString(psarHeaderBuffer, 0, 12);

        if (string.Equals(psarHeader, "PSISOIMG0000", StringComparison.Ordinal))
        {
            discs.Add(new PbpDiscInfo(stream, header.DataPsarOffset, 1));
        }
        else
        {
            // Multi-disc: check for PSTITLEIMG000000
            var fullHeader = Encoding.ASCII.GetString(psarHeaderBuffer, 0, 16);
            if (!string.Equals(fullHeader, "PSTITLEIMG000000", StringComparison.Ordinal))
                return PbpError.InvalidPsarHeader;

            // Skip past the header structure
            var skipBuffer = new byte[4];
            stream.ReadExactly(skipBuffer, 0, 4); // padding
            stream.ReadExactly(skipBuffer, 0, 4); // padding

            // Read and validate magic values
            var magicBuffer = new byte[16];
            stream.ReadExactly(magicBuffer, 0, 16);

            // Skip 0x76 uint32 values
            var dummyBuffer = new byte[4];
            for (var i = 0; i < 0x76; i++)
                stream.ReadExactly(dummyBuffer, 0, 4);

            // Read up to 5 disc positions
            var posBuffer = new byte[20]; // 5 * 4 bytes
            stream.ReadExactly(posBuffer, 0, 20);

            for (var i = 0; i < 5; i++)
            {
                var pos = BitConverter.ToUInt32(posBuffer, i * 4);
                if (pos > 0)
                {
                    discs.Add(new PbpDiscInfo(stream, header.DataPsarOffset + (int)pos, i + 1));
                }
            }
        }

        return PbpError.None;
    }

    /// <summary>
    /// Gets the size of a specific embedded resource.
    /// </summary>
    /// <param name="resourceType">The resource type to query.</param>
    /// <param name="size">When this method returns, contains the resource size in bytes.</param>
    /// <returns>A <see cref="PbpError"/> indicating the result.</returns>
    public PbpError GetResourceSize(PbpResourceType resourceType, out int size)
    {
        size = 0;
        if (_disposed) return PbpError.IoError;

        GetResourceOffsets(resourceType, out var start, out var end);
        if (start < 0 || end <= start)
            return PbpError.ResourceNotFound;

        size = end - start;
        return PbpError.None;
    }

    /// <summary>
    /// Extracts a resource from the PBP file to the specified output path.
    /// </summary>
    /// <param name="resourceType">The resource type to extract.</param>
    /// <param name="outputPath">The file path to write the resource to.</param>
    /// <returns>A <see cref="PbpError"/> indicating the result.</returns>
    public PbpError ExtractResource(PbpResourceType resourceType, string outputPath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        GetResourceOffsets(resourceType, out var start, out var end);
        if (start < 0 || end <= start)
            return PbpError.ResourceNotFound;

        try
        {
            _stream.Seek(start, SeekOrigin.Begin);
            var length = end - start;

            var buffer = ArrayPool<byte>.Shared.Rent(Math.Min(length, 81920));
            try
            {
                using var outputStream = File.Create(outputPath);
                var remaining = length;
                while (remaining > 0)
                {
                    var toRead = Math.Min(buffer.Length, remaining);
                    var read = _stream.Read(buffer, 0, toRead);
                    if (read == 0) break;

                    outputStream.Write(buffer, 0, read);
                    remaining -= read;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            return PbpError.None;
        }
        catch (IOException)
        {
            return PbpError.IoError;
        }
    }

    /// <summary>
    /// Extracts all available resources from the PBP to the specified directory.
    /// </summary>
    /// <param name="outputDirectory">The directory to extract resources into.</param>
    /// <returns>A <see cref="PbpError"/> indicating the result.</returns>
    public PbpError ExtractAllResources(string outputDirectory)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Directory.CreateDirectory(outputDirectory);

        var resources = new (PbpResourceType Type, string FileName)[]
        {
            (PbpResourceType.Icon0, "ICON0.PNG"),
            (PbpResourceType.Icon1, "ICON1.PMF"),
            (PbpResourceType.Pic0, "PIC0.PNG"),
            (PbpResourceType.Pic1, "PIC1.PNG"),
            (PbpResourceType.Snd0, "SND0.AT3")
        };

        foreach (var (type, fileName) in resources)
        {
            GetResourceOffsets(type, out var start, out var end);
            if (start >= 0 && end > start)
            {
                var error = ExtractResource(type, Path.Combine(outputDirectory, fileName));
                if (error != PbpError.None && error != PbpError.ResourceNotFound)
                    return error;
            }
        }

        return PbpError.None;
    }

    /// <summary>
    /// Extracts a specific disc to a BIN file with a CUE sheet.
    /// </summary>
    /// <param name="discIndex">The 1-based disc number to extract.</param>
    /// <param name="binPath">The path for the output BIN file.</param>
    /// <param name="cuePath">The path for the output CUE file. If null, uses binPath with .cue extension.</param>
    /// <param name="progress">Optional callback with bytes written so far.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="PbpError"/> indicating the result.</returns>
    public PbpError ExtractDisc(int discIndex, string binPath, string? cuePath = null, Action<uint>? progress = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var disc = Discs.FirstOrDefault(d => d.Index == discIndex);
        if (disc == null)
            return PbpError.DiscOutOfRange;

        return disc.ExtractToBinCue(binPath, cuePath, progress, cancellationToken);
    }

    /// <summary>
    /// Extracts all discs from a multi-disc PBP. For single-disc PBPs, extracts the single disc.
    /// </summary>
    /// <param name="outputDirectory">The directory where BIN/CUE files will be written.</param>
    /// <param name="baseFileName">The base filename (without extension). If null, uses the PBP title or filename.</param>
    /// <param name="progress">Optional callback with (discIndex, bytesWritten, totalDiscs).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="PbpError"/> indicating the result.</returns>
    public PbpError ExtractAllDiscs(string outputDirectory, string? baseFileName = null, Action<int, uint, int>? progress = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Directory.CreateDirectory(outputDirectory);

        baseFileName ??= Title ?? "disc";

        foreach (var t in Discs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var disc = t;
            var suffix = IsMultiDisc ? $" - Disc {disc.Index}" : "";
            var binPath = Path.Combine(outputDirectory, $"{baseFileName}{suffix}.bin");

            var error = disc.ExtractToBinCue(binPath, progress: bytes => progress?.Invoke(disc.Index, bytes, Discs.Count), cancellationToken: cancellationToken);
            if (error != PbpError.None)
                return error;
        }

        return PbpError.None;
    }

    private void GetResourceOffsets(PbpResourceType type, out int start, out int end)
    {
        switch (type)
        {
            case PbpResourceType.Sfo:
                start = Header.SfoOffset;
                end = Header.Icon0Offset;
                break;
            case PbpResourceType.Icon0:
                start = Header.Icon0Offset;
                end = Header.Icon1Offset;
                break;
            case PbpResourceType.Icon1:
                start = Header.Icon1Offset;
                end = Header.Pic0Offset;
                break;
            case PbpResourceType.Pic0:
                start = Header.Pic0Offset;
                end = Header.Pic1Offset;
                break;
            case PbpResourceType.Pic1:
                start = Header.Pic1Offset;
                end = Header.Snd0Offset;
                break;
            case PbpResourceType.Snd0:
                start = Header.Snd0Offset;
                end = Header.DataPspOffset;
                break;
            case PbpResourceType.DataPsp:
                start = Header.DataPspOffset;
                end = Header.DataPsarOffset;
                break;
            case PbpResourceType.DataPsar:
                start = Header.DataPsarOffset;
                end = (int)_stream.Length;
                break;
            default:
                start = -1;
                end = -1;
                break;
        }
    }

    private static uint ReadUInt32(Stream stream, byte[] buffer)
    {
        stream.ReadExactly(buffer, 0, 4);
        return BitConverter.ToUInt32(buffer, 0);
    }

    private static string ReadNullTerminatedString(Stream stream, int maxLength)
    {
        var buffer = new byte[maxLength];
        var length = 0;

        for (var i = 0; i < maxLength; i++)
        {
            var b = stream.ReadByte();
            if (b <= 0) break;

            buffer[length++] = (byte)b;
        }

        return Encoding.ASCII.GetString(buffer, 0, length);
    }

    /// <summary>
    /// Disposes of the PBP file and releases associated resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;

        if (_ownsStream)
            _stream?.Dispose();

        _stream = null!;
    }
}
