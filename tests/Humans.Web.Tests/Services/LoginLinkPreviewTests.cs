using AwesomeAssertions;
using Humans.Base;
using Humans.Web.Services;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Humans.Web.Tests.Services;

/// <summary>
/// <see cref="LoginLinkPreview"/> maps a login ReturnUrl to the area that owns it, so a shared
/// deep link previews as that area rather than "Sign In".
/// </summary>
public class LoginLinkPreviewTests
{
    [HumansTheory]
    [InlineData("/Shifts", "Shifts")]
    [InlineData("/shifts/42?tab=mine", "Shifts")]
    [InlineData("/Teams/gate#roster", "Teams")]
    [InlineData("/Finance/Transactions", "Finance")]
    [InlineData("/budget", "Budget")]
    public void ResolvesTheFirstSegmentToItsCanonicalArea(string returnUrl, string expected)
    {
        LoginLinkPreview.SegmentFor(returnUrl).Should().Be(expected);
    }

    [HumansTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("/Admin/Users")]
    [InlineData("Shifts")]
    [InlineData("//evil.example/Shifts")]
    [InlineData("https://evil.example/Shifts")]
    public void ReturnsNullForUnknownOrNonLocalTargets(string? returnUrl)
    {
        LoginLinkPreview.SegmentFor(returnUrl).Should().BeNull();
    }

    [HumansFact]
    public void EverySegmentHasTitleAndDescriptionText()
    {
        var factory = new ResourceManagerStringLocalizerFactory(
            Options.Create(new LocalizationOptions()), NullLoggerFactory.Instance);
        var localizer = new StringLocalizer<SharedResource>(factory);

        foreach (var segment in LoginLinkPreview.Segments)
        {
            localizer[$"LinkPreview_{segment}_Title"].ResourceNotFound.Should().BeFalse(segment);
            localizer[$"LinkPreview_{segment}_Description"].ResourceNotFound.Should().BeFalse(segment);
        }
    }
}
