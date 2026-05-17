namespace BatchConvertToCHD.Tests;

public class CancellationHandlingTests
{
    [Fact]
    public void IsCancellationException_TaskCanceledException_ReturnsTrue()
    {
        var ex = new TaskCanceledException("A task was canceled.");

        Assert.True(MainWindow.IsCancellationException(ex));
    }

    [Fact]
    public void IsCancellationException_OperationCanceledException_ReturnsTrue()
    {
        var ex = new OperationCanceledException("Operation was canceled.");

        Assert.True(MainWindow.IsCancellationException(ex));
    }

    [Fact]
    public void IsCancellationException_CanceledCancellationToken_ReturnsTrue()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var ex = Assert.ThrowsAny<OperationCanceledException>(() => cts.Token.ThrowIfCancellationRequested());

        Assert.True(MainWindow.IsCancellationException(ex));
    }

    [Fact]
    public void IsCancellationException_InvalidOperationException_ReturnsFalse()
    {
        var ex = new InvalidOperationException("Something went wrong.");

        Assert.False(MainWindow.IsCancellationException(ex));
    }

    [Fact]
    public void IsCancellationException_IOException_ReturnsFalse()
    {
        var ex = new IOException("Not enough disk space.", -2147024784);

        Assert.False(MainWindow.IsCancellationException(ex));
    }

    [Fact]
    public void IsCancellationException_FormatException_ReturnsFalse()
    {
        var ex = new FormatException("Input string was not in a correct format.");

        Assert.False(MainWindow.IsCancellationException(ex));
    }

    [Fact]
    public void IsDiskSpaceException_IoExceptionDiskFull_ReturnsTrue()
    {
        // HResult -2147024784 = 0x80070070 = ERROR_DISK_FULL
        var ex = new IOException("There is not enough space on the disk.", -2147024784);

        Assert.True(MainWindow.IsDiskSpaceException(ex));
    }

    [Fact]
    public void IsDiskSpaceException_IoExceptionSemTimeout_ReturnsTrue()
    {
        // HResult -2147024783 = 0x80070079 = ERROR_SEM_TIMEOUT
        var ex = new IOException("The semaphore timeout period has expired.", -2147024783);

        Assert.True(MainWindow.IsDiskSpaceException(ex));
    }

    [Fact]
    public void IsDiskSpaceException_IoExceptionOtherHResult_ReturnsFalse()
    {
        var ex = new IOException("File not found.", -2147024894); // 0x80070002 = ERROR_FILE_NOT_FOUND

        Assert.False(MainWindow.IsDiskSpaceException(ex));
    }

    [Fact]
    public void IsDiskSpaceException_NonIoException_ReturnsFalse()
    {
        var ex = new InvalidOperationException("Disk full"); // wrong exception type

        Assert.False(MainWindow.IsDiskSpaceException(ex));
    }

    [Fact]
    public void IsCancellationException_TaskCanceledException_IsDistinctFromDiskSpace()
    {
        // Verifies that TaskCanceledException does NOT match IsDiskSpaceException,
        // preventing the bug where it was being reported as a disk space error
        var ex = new TaskCanceledException("A task was canceled.");

        Assert.True(MainWindow.IsCancellationException(ex));
        Assert.False(MainWindow.IsDiskSpaceException(ex));
    }
}
