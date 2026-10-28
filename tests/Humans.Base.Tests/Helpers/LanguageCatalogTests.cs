using AwesomeAssertions;
using Humans.Base.Extensions;
using Humans.Base.Helpers;
using Xunit;

namespace Humans.Base.Tests.Helpers;

public sealed class LanguageCatalogTests
{
    [HumansTheory]
    [InlineData("en", "German")]
    [InlineData("es", "alemán")]
    [InlineData("de", "Deutsch")]
    [InlineData("it", "tedesco")]
    [InlineData("fr", "allemand")]
    [InlineData("ca", "alemany")]
    public void GetDisplayName_UsesTheViewersCulture(string culture, string expected)
    {
        using var scope = new CultureScope(culture);

        LanguageCatalog.GetDisplayName("de").Should().Be(expected);
    }

    [HumansTheory]
    [InlineData(null, "")]
    [InlineData(" ", "")]
    [InlineData("xx", "xx")]
    public void GetDisplayName_PreservesEmptyAndUnknownCodeFallbacks(string? code, string expected)
    {
        using var scope = new CultureScope("es");

        LanguageCatalog.GetDisplayName(code).Should().Be(expected);
    }
}
