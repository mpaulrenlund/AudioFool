using System.Windows;
using AudioFool.ViewModels;
using Wpf.Ui.Controls;

namespace AudioFool;

/// <summary>
/// Library statistics: headline totals, top artists, file types, audio quality,
/// missing tags and decades. Read-only - it opens a snapshot and writes nothing.
/// <para>
/// Modal like <see cref="TagEditWindow"/>, so the snapshot cannot drift out of
/// date behind it while a background rescan lands. Escape or Close dismisses it.
/// </para>
/// </summary>
public partial class StatisticsWindow : FluentWindow
{
    public StatisticsWindow(StatisticsViewModel viewModel, Window owner)
    {
        DataContext = viewModel;

        InitializeComponent();

        Owner = owner;

        // Opens as tall as its content, but never taller than the screen - past
        // that the content scrolls. The cap is lifted once open, so the window
        // can still be resized or maximised like any other.
        MaxHeight = SystemParameters.WorkArea.Height;

        Loaded += (_, _) =>
        {
            CenterOverOwner(owner);
            SizeToContent = SizeToContent.Manual;
            ClearValue(MaxHeightProperty);
        };
    }

    private void CenterOverOwner(Window owner)
    {
        var work = SystemParameters.WorkArea;

        var centreX = owner.Left + (owner.ActualWidth / 2);
        var centreY = owner.Top + (owner.ActualHeight / 2);

        Left = Math.Clamp(centreX - (ActualWidth / 2), work.Left, Math.Max(work.Left, work.Right - ActualWidth));
        Top = Math.Clamp(centreY - (ActualHeight / 2), work.Top, Math.Max(work.Top, work.Bottom - ActualHeight));
    }

    /// <summary>
    /// Closes with the row as the answer. The window stays a modal snapshot, so
    /// the caller applies the choice once it is gone rather than the window
    /// reaching into the main view while it is still open.
    /// </summary>
    private void Row_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: BarRow { IsClickable: true } row }
            || DataContext is not StatisticsViewModel vm)
            return;

        vm.Chosen = row;
        DialogResult = true;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
