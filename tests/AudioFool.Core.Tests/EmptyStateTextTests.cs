using AudioFool.Core.Library;

namespace AudioFool.Core.Tests;

/// <summary>
/// What the Songs panel says with no album selected. It must not tell the user to
/// pick an artist when there is none to pick.
/// </summary>
public class EmptyStateTextTests
{
    [Theory]
    // hasAny, ticked, narrowed, artists, scanning -> title
    [InlineData(false, false, false, false, false, "No music yet")]
    [InlineData(false, false, false, false, true, "Scanning the library")]
    [InlineData(false, false, true, false, false, "No music yet")]
    [InlineData(true, false, false, false, false, "Every folder is unticked")]
    [InlineData(true, false, true, false, true, "Every folder is unticked")]
    [InlineData(true, true, true, false, false, "No matches")]
    [InlineData(true, true, false, true, false, "No album selected")]
    [InlineData(true, true, true, true, false, "No album selected")]
    [InlineData(true, true, false, false, false, "No album selected")]
    public void Title_follows_why_nothing_is_selected(
        bool hasAny, bool ticked, bool narrowed, bool artists, bool scanning, string expected) =>
        Assert.Equal(expected, EmptyStateText.Describe(hasAny, ticked, narrowed, artists, scanning).Title);

    [Fact]
    public void Only_the_ordinary_case_says_pick_an_artist()
    {
        Assert.Equal("Pick an artist, then one of their albums.",
            EmptyStateText.Describe(true, true, false, true, false).Detail);
        Assert.DoesNotContain("artist", EmptyStateText.Describe(false, false, false, false, false).Detail);
        Assert.DoesNotContain("artist", EmptyStateText.Describe(true, true, true, false, false).Detail);
    }
}
