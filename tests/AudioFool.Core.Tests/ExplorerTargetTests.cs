using AudioFool.Core.Library;

namespace AudioFool.Core.Tests;

public sealed class ExplorerTargetTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"AudioFool-explorer-{Guid.NewGuid():N}");

    public ExplorerTargetTests() => Directory.CreateDirectory(Path.Combine(_folder, "[2014] Album, Deluxe"));

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void An_existing_file_is_selected_in_its_folder()
    {
        var file = Path.Combine(_folder, "[2014] Album, Deluxe", "01 Song.flac");
        File.WriteAllBytes(file, [0]);

        Assert.Equal($"/select,\"{file}\"", ExplorerTarget.ArgumentsFor(file));
    }

    [Fact]
    public void A_missing_file_opens_its_folder()
    {
        var folder = Path.Combine(_folder, "[2014] Album, Deluxe");

        Assert.Equal($"\"{folder}\"", ExplorerTarget.ArgumentsFor(Path.Combine(folder, "gone.flac")));
    }

    [Fact]
    public void A_missing_folder_opens_the_nearest_one_above_it()
    {
        var file = Path.Combine(_folder, "Moved Artist", "Moved Album", "gone.flac");

        Assert.Equal($"\"{_folder}\"", ExplorerTarget.ArgumentsFor(file));
    }

    [Fact]
    public void Nothing_opens_when_the_drive_is_absent()
    {
        var missing = Enumerable.Range('F', 'Z' - 'F' + 1)
            .Select(c => $"{(char)c}:\\")
            .FirstOrDefault(root => !Directory.Exists(root));
        Assert.NotNull(missing);

        Assert.Null(ExplorerTarget.ArgumentsFor(Path.Combine(missing, "Music", "Artist", "song.flac")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_opens_for_an_empty_path(string path) =>
        Assert.Null(ExplorerTarget.ArgumentsFor(path));
}
