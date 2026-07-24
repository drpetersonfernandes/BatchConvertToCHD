using System.Text;
using PBPSharp;

namespace BatchConvertToCHD.Tests;

public class PbpFileTests : IDisposable
{
    private readonly string _tempDir;

    public PbpFileTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"PbpFileTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, true);
        }
        catch { /* ignore */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void OpenNonExistentFileReturnsFileNotFound()
    {
        var path = Path.Combine(_tempDir, "nonexistent.pbp");
        var error = PbpFile.Open(path, out var pbp);
        Assert.Equal(PbpError.FileNotFound, error);
        Assert.Null(pbp);
    }

    [Fact]
    public void OpenEmptyFileReturnsInvalidHeader()
    {
        var path = Path.Combine(_tempDir, "empty.pbp");
        File.WriteAllBytes(path, []);
        var error = PbpFile.Open(path, out var pbp);
        Assert.Equal(PbpError.InvalidHeader, error);
        Assert.Null(pbp);
    }

    [Fact]
    public void OpenFileWithWrongMagicReturnsInvalidHeader()
    {
        var path = Path.Combine(_tempDir, "wrongmagic.pbp");
        var data = new byte[100];
        BitConverter.GetBytes(0x12345678u).CopyTo(data, 0);
        File.WriteAllBytes(path, data);
        var error = PbpFile.Open(path, out var pbp);
        Assert.Equal(PbpError.InvalidHeader, error);
        Assert.Null(pbp);
    }

    [Fact]
    public void OpenFileWithShortHeaderReturnsInvalidHeader()
    {
        var path = Path.Combine(_tempDir, "short.pbp");
        var data = new byte[20]; // less than HeaderSize (40)
        BitConverter.GetBytes(PbpHeader.MagicValue).CopyTo(data, 0);
        File.WriteAllBytes(path, data);
        var error = PbpFile.Open(path, out var pbp);
        Assert.Equal(PbpError.InvalidHeader, error);
        Assert.Null(pbp);
    }

    [Fact]
    public void OpenFileWithInvalidPsarReturnsInvalidPsarHeader()
    {
        var path = CreatePbpWithInvalidPsar();
        var error = PbpFile.Open(path, out var pbp);
        Assert.Equal(PbpError.InvalidPsarHeader, error);
        Assert.Null(pbp);
    }

    [Fact]
    public void DefaultPbpHeaderIsNotValid()
    {
        var header = default(PbpHeader);
        Assert.False(header.IsValid);
    }

    [Fact]
    public void PbpHeaderMagicValueIsCorrect()
    {
        Assert.Equal(0x00504250u, PbpHeader.MagicValue);
    }

    [Fact]
    public void PbpHeaderSizeIs40()
    {
        Assert.Equal(0x28, PbpHeader.HeaderSize);
    }

    [Fact]
    public void PbpHeaderConstructorSetsAllProperties()
    {
        var header = new PbpHeader(
            1,
            0x28,
            0x100,
            0x200,
            0x300,
            0x400,
            0x500,
            0x600,
            0x700);

        Assert.Equal(1u, header.Version);
        Assert.Equal(0x28, header.SfoOffset);
        Assert.Equal(0x100, header.Icon0Offset);
        Assert.Equal(0x200, header.Icon1Offset);
        Assert.Equal(0x300, header.Pic0Offset);
        Assert.Equal(0x400, header.Pic1Offset);
        Assert.Equal(0x500, header.Snd0Offset);
        Assert.Equal(0x600, header.DataPspOffset);
        Assert.Equal(0x700, header.DataPsarOffset);
        Assert.True(header.IsValid);
    }

    [Fact]
    public void SfoDataDefaultValuesAreCorrect()
    {
        var sfo = new SfoData();
        Assert.Equal(0u, sfo.Magic);
        Assert.Equal(0u, sfo.Version);
        Assert.Equal(0u, sfo.KeyTableOffset);
        Assert.Equal(0u, sfo.DataTableOffset);
        Assert.NotNull(sfo.Entries);
        Assert.Empty(sfo.Entries);
    }

    [Fact]
    public void SfoDataGetStringReturnsNullForMissingKey()
    {
        var sfo = new SfoData();
        Assert.Null(sfo.GetString("NONEXISTENT"));
    }

    [Fact]
    public void SfoDataGetUInt32ReturnsNullForMissingKey()
    {
        var sfo = new SfoData();
        Assert.Null(sfo.GetUInt32("NONEXISTENT"));
    }

    [Fact]
    public void SfoDataKeysClassHasExpectedConstants()
    {
        Assert.Equal("BOOTABLE", SfoData.Keys.Bootable);
        Assert.Equal("CATEGORY", SfoData.Keys.Category);
        Assert.Equal("DISC_ID", SfoData.Keys.DiscId);
        Assert.Equal("TITLE", SfoData.Keys.Title);
    }

    [Fact]
    public void SfoEntryDefaultValuesAreCorrect()
    {
        var entry = new SfoEntry();
        Assert.Equal(string.Empty, entry.Key);
        Assert.Equal(0, entry.Format);
        Assert.Equal(0u, entry.Length);
        Assert.Equal(0u, entry.MaxLength);
        Assert.Null(entry.Value);
    }

    [Fact]
    public void TocEntryDefaultValuesAreCorrect()
    {
        var entry = new TocEntry();
        Assert.Equal(0, (int)entry.TrackType);
        Assert.Equal(0, entry.TrackNo);
        Assert.Equal(0, entry.Minutes);
        Assert.Equal(0, entry.Seconds);
        Assert.Equal(0, entry.Frames);
    }

    [Fact]
    public void TrackTypeDataValue()
    {
        Assert.Equal(0x41, (int)TrackType.Data);
    }

    [Fact]
    public void TrackTypeAudioValue()
    {
        Assert.Equal(0x01, (int)TrackType.Audio);
    }

    [Fact]
    public void PbpErrorEnumHasExpectedValues()
    {
        Assert.Equal(0, (int)PbpError.None);
        Assert.Equal(1, (int)PbpError.InvalidHeader);
        Assert.Equal(2, (int)PbpError.FileNotFound);
        Assert.Equal(3, (int)PbpError.IoError);
        Assert.Equal(4, (int)PbpError.CorruptFile);
        Assert.Equal(5, (int)PbpError.InvalidPsarHeader);
        Assert.Equal(6, (int)PbpError.DiscOutOfRange);
        Assert.Equal(7, (int)PbpError.ResourceNotFound);
        Assert.Equal(8, (int)PbpError.DecompressionError);
    }

    [Fact]
    public void PbpResourceTypeEnumHasExpectedValues()
    {
        Assert.Equal(0, (int)PbpResourceType.Sfo);
        Assert.Equal(1, (int)PbpResourceType.Icon0);
        Assert.Equal(2, (int)PbpResourceType.Icon1);
        Assert.Equal(3, (int)PbpResourceType.Pic0);
        Assert.Equal(4, (int)PbpResourceType.Pic1);
        Assert.Equal(5, (int)PbpResourceType.Snd0);
        Assert.Equal(6, (int)PbpResourceType.DataPsp);
        Assert.Equal(7, (int)PbpResourceType.DataPsar);
    }

    [Fact]
    public void PbpDiscInfoIsoBlockSizeIsCorrect()
    {
        Assert.Equal(0x930, PbpDiscInfo.IsoBlockSize);
    }

    private string CreatePbpWithInvalidPsar()
    {
        var path = Path.Combine(_tempDir, $"test_{Guid.NewGuid():N}.pbp");

        const int sfoOffset = 0x28;
        const int dataPsarOffset = 0x200;

        using var ms = new MemoryStream();

        // PBP Header (40 bytes)
        ms.Write(BitConverter.GetBytes(PbpHeader.MagicValue));
        ms.Write(BitConverter.GetBytes(1u)); // version
        ms.Write(BitConverter.GetBytes(sfoOffset));
        ms.Write(BitConverter.GetBytes(0x100)); // icon0
        ms.Write(BitConverter.GetBytes(0x100)); // icon1
        ms.Write(BitConverter.GetBytes(0x100)); // pic0
        ms.Write(BitConverter.GetBytes(0x100)); // pic1
        ms.Write(BitConverter.GetBytes(0x100)); // snd0
        ms.Write(BitConverter.GetBytes(0x100)); // dataPsp
        ms.Write(BitConverter.GetBytes(dataPsarOffset)); // dataPsar

        // Minimal SFO at 0x28
        ms.Write(BuildMinimalSfo());

        // Pad to dataPsarOffset
        while (ms.Position < dataPsarOffset)
            ms.WriteByte(0);

        // Write invalid PSAR header (not PSISOIMG0000 or PSTITLEIMG000000)
        var invalidHeader = "INVALID_PSAR!"u8.ToArray();
        ms.Write(invalidHeader);
        ms.Write(new byte[4]); // pad to 16

        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }

    private static byte[] BuildMinimalSfo()
    {
        using var ms = new MemoryStream();

        // SFO Header (16 bytes)
        ms.Write(BitConverter.GetBytes(0x46535000u)); // magic
        ms.Write(BitConverter.GetBytes(0x00000101u)); // version
        var keyTableOffsetPos = ms.Position;
        ms.Write(BitConverter.GetBytes(0u)); // placeholder
        var dataTableOffsetPos = ms.Position;
        ms.Write(BitConverter.GetBytes(0u)); // placeholder
        var entryCountPos = ms.Position;
        ms.Write(BitConverter.GetBytes(0u)); // placeholder

        var entries = new List<(string Key, ushort Format, byte[] Data)>
        {
            ("TITLE", 0x0204, "Test Game"u8.ToArray())
        };

        var entryCount = (uint)entries.Count;

        var keyTable = new MemoryStream();
        var dataTable = new MemoryStream();
        var dirEntries = new List<byte[]>();

        foreach (var (key, format, data) in entries)
        {
            var keyOffset = (ushort)keyTable.Position;
            var dataOffset = (uint)dataTable.Position;

            keyTable.Write(Encoding.ASCII.GetBytes(key));
            keyTable.WriteByte(0);

            dataTable.Write(data);

            var dirEntry = new byte[16];
            BitConverter.GetBytes(keyOffset).CopyTo(dirEntry, 0);
            BitConverter.GetBytes(format).CopyTo(dirEntry, 2);
            BitConverter.GetBytes((uint)data.Length).CopyTo(dirEntry, 4);
            BitConverter.GetBytes((uint)Math.Max(data.Length, 32)).CopyTo(dirEntry, 8);
            BitConverter.GetBytes(dataOffset).CopyTo(dirEntry, 12);
            dirEntries.Add(dirEntry);
        }

        foreach (var dirEntry in dirEntries)
            ms.Write(dirEntry);

        var keyTableOffset = 16 + entryCount * 16;
        var dataTableOffset = (uint)(keyTableOffset + keyTable.Length);

        ms.Write(keyTable.ToArray());
        ms.Write(dataTable.ToArray());

        var sfoBytes = ms.ToArray();
        BitConverter.GetBytes(keyTableOffset).CopyTo(sfoBytes, (int)keyTableOffsetPos);
        BitConverter.GetBytes(dataTableOffset).CopyTo(sfoBytes, (int)dataTableOffsetPos);
        BitConverter.GetBytes(entryCount).CopyTo(sfoBytes, (int)entryCountPos);

        return sfoBytes;
    }
}
