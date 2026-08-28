using CommunityToolkit.Mvvm.ComponentModel;

namespace AudioFool.ViewModels;

public sealed partial class ThemeItem : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected;

    public string Name { get; }

    public ThemeItem(string name, bool selected)
    {
        Name = name;
        _isSelected = selected;
    }
}
