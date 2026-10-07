using System.Windows;
using Wpf.Ui.Controls;

namespace AudioFool;

/// <summary>
/// Asks for a playlist name, or for a yes before something is deleted. Modal,
/// centred over its owner like the other dialogs.
/// </summary>
public partial class PromptWindow : FluentWindow
{
    private readonly Func<string, string?>? _validate;

    private PromptWindow(Window owner, string title, string message, string okLabel,
                         string? initialText, Func<string, string?>? validate)
    {
        InitializeComponent();

        Owner = owner;
        Title = title;
        OkButton.Content = okLabel;
        _validate = validate;

        MessageText.Text = message;
        MessageText.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

        if (initialText is null)
        {
            NameBox.Visibility = Visibility.Collapsed;
        }
        else
        {
            NameBox.Text = initialText;
            if (message.Length > 0)
                NameBox.Margin = new Thickness(0, Theming.TokenResources.Current!.Numbers["dialog.fieldGap"], 0, 0);
        }

        Loaded += (_, _) =>
        {
            CenterOverOwner(owner);
            if (initialText is not null)
            {
                NameBox.Focus();
                NameBox.SelectAll();
            }
            else
            {
                OkButton.Focus();
            }
        };
    }

    /// <summary>A name, checked by <paramref name="validate"/>; null if cancelled.</summary>
    public static string? AskName(Window owner, string title, string okLabel, string initialText,
                                  Func<string, string?> validate)
    {
        var window = new PromptWindow(owner, title, "", okLabel, initialText, validate);
        return window.ShowDialog() == true ? window.NameBox.Text.Trim() : null;
    }

    /// <summary>True when the user chose <paramref name="okLabel"/>.</summary>
    public static bool Confirm(Window owner, string title, string message, string okLabel) =>
        new PromptWindow(owner, title, message, okLabel, null, null).ShowDialog() == true;

    private void NameBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        ErrorText.Visibility = Visibility.Collapsed;

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (_validate?.Invoke(NameBox.Text) is { } problem)
        {
            ErrorText.Text = problem;
            ErrorText.Visibility = Visibility.Visible;
            NameBox.Focus();
            return;
        }

        DialogResult = true;
    }

    private void CenterOverOwner(Window owner)
    {
        var work = SystemParameters.WorkArea;

        var centreX = owner.Left + (owner.ActualWidth / 2);
        var centreY = owner.Top + (owner.ActualHeight / 2);

        Left = Math.Clamp(centreX - (Width / 2), work.Left, Math.Max(work.Left, work.Right - Width));
        Top = Math.Clamp(centreY - (ActualHeight / 2), work.Top, Math.Max(work.Top, work.Bottom - ActualHeight));
    }
}
