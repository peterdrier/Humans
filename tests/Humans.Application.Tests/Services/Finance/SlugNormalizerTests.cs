using AwesomeAssertions;
using Humans.Application.Services.Finance;
using Humans.Testing;
using Xunit;

namespace Humans.Application.Tests.Services.Finance;

public class SlugNormalizerTests
{
    [HumansTheory]
    [InlineData("Sound", "sound")]
    [InlineData("Sonido y Música", "sonido-y-musica")]
    [InlineData("  Departments  ", "departments")]
    [InlineData("Site Power & Lighting", "site-power-lighting")]
    [InlineData("Año 2026", "ano-2026")]
    [InlineData("Niño", "nino")]
    [InlineData("Café/Bar", "cafe-bar")]
    [InlineData("multi   spaces", "multi-spaces")]
    [InlineData("--leading-and-trailing--", "leading-and-trailing")]
    [InlineData("ÑOÑO", "nono")]
    public void Normalize_ProducesHoldedSafeSlug(string input, string expected)
    {
        SlugNormalizer.Normalize(input).Should().Be(expected);
    }

    [HumansFact]
    public void Normalize_IsIdempotent()
    {
        var once = SlugNormalizer.Normalize("Sonido y Música");
        var twice = SlugNormalizer.Normalize(once);
        twice.Should().Be(once);
    }

    [HumansTheory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("***")]
    public void Normalize_OnEmptyOrSymbolOnly_ReturnsEmptyString(string input)
    {
        SlugNormalizer.Normalize(input).Should().Be(string.Empty);
    }

    [HumansFact]
    public void Normalize_OnNull_ReturnsEmptyString()
    {
        SlugNormalizer.Normalize(null!).Should().Be(string.Empty);
    }
}
