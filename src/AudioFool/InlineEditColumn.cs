using System.Windows.Controls;

namespace AudioFool;

/// <summary>
/// A text column that can be edited although its binding is one-way.
/// <para>
/// <see cref="DataGridBoundColumn"/> makes any column with a one-way binding
/// read-only, and these have to be one-way: <c>Track</c> is immutable, and
/// Song shows <c>DisplayTitle</c>, which has no setter at all. The typed text
/// never goes back through the binding. <c>MainWindow</c> takes it from the
/// edit box when the edit ends and saves it to the file, and the save replaces
/// the row with the updated track.
/// </para>
/// </summary>
public sealed class InlineEditColumn : DataGridTextColumn
{
    protected override bool OnCoerceIsReadOnly(bool baseValue) =>
        baseValue || DataGridOwner?.IsReadOnly == true;
}
