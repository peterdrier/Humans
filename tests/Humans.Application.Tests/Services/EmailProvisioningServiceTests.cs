using AwesomeAssertions;
using Humans.Infrastructure.Services;
using Xunit;

namespace Humans.Application.Tests.Services;

public class EmailProvisioningServiceTests
{
    [Theory]
    [InlineData("mueller", "mueller")]
    [InlineData("müller", "mueller")]
    [InlineData("Müller", "mueller")]
    [InlineData("schön", "schoen")]
    [InlineData("Ärzte", "aerzte")]
    [InlineData("straße", "strasse")]
    public void SanitizeEmailPrefix_TransliteratesGermanCharacters(string input, string expected)
    {
        EmailProvisioningService.SanitizeEmailPrefix(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("garcía", "garcia")]
    [InlineData("café", "cafe")]
    [InlineData("naïve", "naive")]
    [InlineData("résumé", "resume")]
    [InlineData("señor", "senor")]
    public void SanitizeEmailPrefix_StripsAccentsViaNfdDecomposition(string input, string expected)
    {
        EmailProvisioningService.SanitizeEmailPrefix(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("müller.garcía", "mueller.garcia")]
    [InlineData("Böhm-López", "boehm-lopez")]
    public void SanitizeEmailPrefix_HandlesMixedGermanAndAccented(string input, string expected)
    {
        EmailProvisioningService.SanitizeEmailPrefix(input).Should().Be(expected);
    }

    [Fact]
    public void SanitizeEmailPrefix_ReturnsNullForNonTransliterableCharacters()
    {
        EmailProvisioningService.SanitizeEmailPrefix("田中").Should().BeNull();
    }

    [Fact]
    public void SanitizeEmailPrefix_ReturnsEmptyForWhitespaceOnly()
    {
        EmailProvisioningService.SanitizeEmailPrefix("   ").Should().BeEmpty();
    }

    [Fact]
    public void SanitizeEmailPrefix_ReturnsNullForEmbeddedSpaces()
    {
        EmailProvisioningService.SanitizeEmailPrefix("jo hn").Should().BeNull();
    }

    [Fact]
    public void SanitizeEmailPrefix_TrimsLeadingAndTrailingWhitespace()
    {
        EmailProvisioningService.SanitizeEmailPrefix("  alice  ").Should().Be("alice");
    }

    [Fact]
    public void SanitizeEmailPrefix_ConvertsToLowerCase()
    {
        EmailProvisioningService.SanitizeEmailPrefix("Alice").Should().Be("alice");
    }

    [Fact]
    public void SanitizeEmailPrefix_ReturnsNullForEmbeddedControlCharacters()
    {
        EmailProvisioningService.SanitizeEmailPrefix("te\tst").Should().BeNull();
    }
}
