using AwesomeAssertions;
using Humans.Application.Helpers;
using Xunit;

namespace Humans.Application.Tests.Helpers;

public class SlugHelperTests
{
    [HumansTheory]
    [InlineData("Camp Funhouse", "camp-funhouse")]
    [InlineData("  Spaced  Out  ", "spaced-out")]
    [InlineData("Über Cämp", "ber-c-mp")]
    [InlineData("LOUD & PROUD!!!", "loud-proud")]
    [InlineData("---dashes---", "dashes")]
    public void GenerateSlug_VariousInputs_ReturnsExpectedSlug(string input, string expected)
    {
        var result = SlugHelper.GenerateSlug(input);
        result.Should().Be(expected);
    }

    [HumansTheory]
    [InlineData("register")]
    [InlineData("admin")]
    [InlineData("Register")]
    [InlineData("ADMIN")]
    public void IsReservedSlug_ReservedNames_ReturnsTrue(string slug)
    {
        SlugHelper.IsReservedCampSlug(slug).Should().BeTrue();
    }

    [HumansFact]
    public void IsReservedSlug_NormalName_ReturnsFalse()
    {
        SlugHelper.IsReservedCampSlug("camp-funhouse").Should().BeFalse();
    }
}
