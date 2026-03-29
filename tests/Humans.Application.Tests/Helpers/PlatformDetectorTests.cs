using AwesomeAssertions;
using Humans.Domain.Helpers;
using Xunit;

namespace Humans.Application.Tests.Helpers;

public class PlatformDetectorTests
{
    [Theory]
    [InlineData("https://www.instagram.com/campvibes", "Instagram", "fa-brands fa-instagram")]
    [InlineData("https://facebook.com/campvibes", "Facebook", "fa-brands fa-facebook")]
    [InlineData("https://x.com/campvibes", "X", "fa-brands fa-x-twitter")]
    [InlineData("https://twitter.com/campvibes", "X", "fa-brands fa-x-twitter")]
    [InlineData("https://tiktok.com/@campvibes", "TikTok", "fa-brands fa-tiktok")]
    [InlineData("https://discord.gg/abc123", "Discord", "fa-brands fa-discord")]
    [InlineData("https://discord.com/invite/abc", "Discord", "fa-brands fa-discord")]
    [InlineData("https://youtube.com/c/campvibes", "YouTube", "fa-brands fa-youtube")]
    [InlineData("https://linkedin.com/company/campvibes", "LinkedIn", "fa-brands fa-linkedin")]
    [InlineData("https://campvibes.com", "campvibes.com", "fa-solid fa-link")]
    [InlineData("https://www.campvibes.org/about", "campvibes.org", "fa-solid fa-link")]
    public void Detect_ReturnsCorrectPlatform(string url, string expectedName, string expectedIcon)
    {
        var result = PlatformDetector.Detect(url);
        result.Name.Should().Be(expectedName);
        result.IconClass.Should().Be(expectedIcon);
    }

    [Theory]
    [InlineData("https://campvibes.com", false)]
    [InlineData("https://instagram.com/campvibes", true)]
    [InlineData("https://facebook.com/campvibes", true)]
    [InlineData("https://mycampsite.org", false)]
    public void IsSocialMedia_IdentifiesCorrectly(string url, bool expected)
    {
        PlatformDetector.IsSocialMedia(url).Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-url")]
    public void Detect_InvalidUrl_ReturnsFallback(string url)
    {
        var result = PlatformDetector.Detect(url);
        result.Name.Should().BeEmpty();
        result.IconClass.Should().Be("fa-solid fa-link");
    }
}
