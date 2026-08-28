using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AudioFool.ViewModels;

public sealed partial class FolderFilterItem : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    private string _folderPath;

    [ObservableProperty]
    private bool _isEnabled;

    public string DisplayName
    {
        get
        {
            var name = Path.GetFileName(
                FolderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return string.IsNullOrEmpty(name) ? FolderPath : name;
        }
    }

    public FolderFilterItem(string path, bool enabled)
    {
        _folderPath = path;
        _isEnabled = enabled;
    }
}
