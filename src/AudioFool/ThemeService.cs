using System.Windows;
using Wpf.Ui.Controls;

namespace AudioFool;

public static class ThemeService
{
    private static ResourceDictionary? _overlay;

    public static void Apply(string theme)
    {
        var app = Application.Current;

        if (_overlay is not null)
        {
            app.Resources.MergedDictionaries.Remove(_overlay);
            _overlay = null;
        }

        var backdrop = WindowBackdropType.Mica;

        if (theme == "Vista")
        {
            _overlay = new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/Themes/VistaTheme.xaml")
            };
            app.Resources.MergedDictionaries.Add(_overlay);
            backdrop = WindowBackdropType.Acrylic;
        }

        if (app.MainWindow is FluentWindow window)
            window.WindowBackdropType = backdrop;
    }
}
