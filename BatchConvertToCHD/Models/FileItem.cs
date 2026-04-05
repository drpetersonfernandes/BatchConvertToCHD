using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace BatchConvertToCHD.Models;

public class FileItem : INotifyPropertyChanged
{
    private bool _isSelected = true;
    private string _fileName = string.Empty;
    private string _fullPath = string.Empty;
    private long _fileSize;
    private string _displaySize = string.Empty;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;

            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public string FileName
    {
        get => _fileName;
        set
        {
            if (_fileName == value) return;

            _fileName = value;
            OnPropertyChanged();
        }
    }

    public string FullPath
    {
        get => _fullPath;
        set
        {
            if (_fullPath == value) return;

            _fullPath = value;
            OnPropertyChanged();
        }
    }

    public long FileSize
    {
        get => _fileSize;
        set
        {
            if (_fileSize == value) return;

            _fileSize = value;
            OnPropertyChanged();
            DisplaySize = FormatSize(value);
        }
    }

    public string DisplaySize
    {
        get => _displaySize;
        set
        {
            if (_displaySize == value) return;

            _displaySize = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private static string FormatSize(long bytes)
    {
        string[] suffix = ["B", "KB", "MB", "GB", "TB"];
        int i;
        double dblSByte = bytes;
        for (i = 0; i < suffix.Length && bytes >= 1024; i++, bytes /= 1024)
        {
            dblSByte = bytes / 1024.0;
        }

        return $"{dblSByte:0.##} {suffix[i]}";
    }
}
