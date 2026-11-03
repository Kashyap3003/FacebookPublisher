using FacebookPublisher.Application.Groups;

namespace FacebookPublisher.Tests;

public class GroupUrlNormalizerTests
{
    [Theory]
    [InlineData("https://www.facebook.com/groups/123456789", "https://www.facebook.com/groups/123456789")]
    [InlineData("https://www.facebook.com/groups/123456789/", "https://www.facebook.com/groups/123456789")]
    [InlineData("facebook.com/groups/LocalMakers", "https://www.facebook.com/groups/localmakers")]
    [InlineData("https://m.facebook.com/groups/local.makers/?ref=share", "https://www.facebook.com/groups/local.makers")]
    [InlineData("http://web.facebook.com/groups/abc_def/permalink/999", "https://www.facebook.com/groups/abc_def")]
    [InlineData("  https://fb.com/groups/xyz  ", "https://www.facebook.com/groups/xyz")]
    public void Valid_links_are_normalized(string input, string expected)
    {
        Assert.True(GroupUrlNormalizer.TryNormalize(input, out var normalized, out var error), error);
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("https://www.facebook.com/share/g/1FMskdPL2Z/", "https://www.facebook.com/share/g/1FMskdPL2Z/")]
    [InlineData("https://www.facebook.com/share/g/1FEEdt983u", "https://www.facebook.com/share/g/1FEEdt983u/")]
    [InlineData("facebook.com/share/g/1GtRXQEUEy/?mibextid=K35XfP", "https://www.facebook.com/share/g/1GtRXQEUEy/")]
    [InlineData("https://m.facebook.com/share/1AbCdEf/", "https://www.facebook.com/share/1AbCdEf/")]
    public void Share_links_are_recognised_and_keep_their_case(string input, string expected)
    {
        Assert.True(GroupUrlNormalizer.TryParseShareLink(input, out var shareUrl));
        Assert.Equal(expected, shareUrl);
    }

    [Theory]
    [InlineData("https://www.facebook.com/groups/123")]
    [InlineData("https://example.com/share/g/1FMskdPL2Z/")]
    [InlineData("https://www.facebook.com/share/g/")]
    [InlineData("https://www.facebook.com/share/g/bad code!/")]
    public void Non_share_links_are_not_treated_as_share_links(string input)
    {
        Assert.False(GroupUrlNormalizer.TryParseShareLink(input, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a url at all")]
    [InlineData("https://example.com/groups/123")]
    [InlineData("https://www.facebook.com/some.profile")]
    [InlineData("https://www.facebook.com/groups/feed/")]
    [InlineData("https://www.facebook.com/groups/discover")]
    [InlineData("https://www.facebook.com/share/g/1AbCdEf/")]
    [InlineData("https://www.facebook.com/groups/")]
    [InlineData("ftp://www.facebook.com/groups/123")]
    public void Invalid_links_are_rejected(string input)
    {
        Assert.False(GroupUrlNormalizer.TryNormalize(input, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }
}
