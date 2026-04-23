using BatchConvertToCHD.Models;

namespace BatchConvertToCHD.Tests;

public class FileItemTests
{
    [Fact]
    public void IsSelectedDefaultValueIsTrue()
    {
        var item = new FileItem();
        Assert.True(item.IsSelected);
    }

    [Fact]
    public void IsSelectedPropertyChangedFiresEvent()
    {
        var item = new FileItem();
        var fired = false;
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FileItem.IsSelected))
            {
                fired = true;
            }
        };

        item.IsSelected = false;

        Assert.True(fired);
    }

    [Fact]
    public void IsSelectedSameValueDoesNotFireEvent()
    {
        var item = new FileItem { IsSelected = true };
        var fired = false;
        item.PropertyChanged += (_, _) => { fired = true; };

        item.IsSelected = true;

        Assert.False(fired);
    }

    [Fact]
    public void FileNamePropertyChangedFiresEvent()
    {
        var item = new FileItem();
        var fired = false;
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FileItem.FileName))
            {
                fired = true;
            }
        };

        item.FileName = "test.iso";

        Assert.True(fired);
    }

    [Fact]
    public void FullPathPropertyChangedFiresEvent()
    {
        var item = new FileItem();
        var fired = false;
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FileItem.FullPath))
            {
                fired = true;
            }
        };

        item.FullPath = @"C:\test.iso";

        Assert.True(fired);
    }

    [Fact]
    public void FileSizeSetsDisplaySize()
    {
        var item = new FileItem();
        var fired = false;
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FileItem.DisplaySize))
            {
                fired = true;
            }
        };

        item.FileSize = 1536; // 1.5 KB

        Assert.True(fired);
        Assert.Equal("1.5 KB", item.DisplaySize);
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1048576, "1 MB")]
    [InlineData(1073741824, "1 GB")]
    public void DisplaySizeFormatsCorrectly(long bytes, string expected)
    {
        var item = new FileItem
        {
            // Set to a non-zero value first to ensure the setter runs even when testing 0
            FileSize = 1
        };
        item.FileSize = bytes;
        Assert.Equal(expected, item.DisplaySize);
    }
}
