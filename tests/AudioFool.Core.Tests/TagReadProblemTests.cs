using AudioFool.Core.Library;

namespace AudioFool.Core.Tests;

/// <summary>
/// What a tag window says when none of its songs could be opened: a missing drive
/// is named as such, anything else is a plain "couldn't be read".
/// </summary>
public class TagReadProblemTests
{
    private static bool OnlyC(string root) => root.Equals(@"C:\", StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void A_missing_drive_is_named()
    {
        Assert.Equal("The music drive isn't connected, so these songs can't be read or saved.",
            TagReadProblem.Describe([@"D:\Music\A\1.flac", @"D:\Music\A\2.flac"], OnlyC));
        Assert.Equal("The music drive isn't connected, so this song can't be read or saved.",
            TagReadProblem.Describe([@"D:\Music\A\1.flac"], OnlyC));
    }

    [Fact]
    public void A_drive_that_is_there_gives_the_plain_reason()
    {
        Assert.Equal("These songs couldn't be read, so they can't be saved.",
            TagReadProblem.Describe([@"C:\Music\A\1.flac", @"C:\Music\A\2.flac"], OnlyC));
        Assert.Equal("This song couldn't be read, so it can't be saved.",
            TagReadProblem.Describe([@"C:\Music\A\1.flac"], OnlyC));
    }
}
