using AwesomeAssertions;
using Humans.Base.Extensions;
using Humans.Base.Helpers;
using Xunit;

namespace Humans.Base.Tests.Helpers;

public sealed class LanguageCatalogTests
{
    [HumansFact]
    public void GetDisplayName_IsEnglishInTheDefaultCulture()
    {
        using var scope = new CultureScope(CultureCatalog.DefaultCultureCode);

        LanguageCatalog.GetDisplayName("de").Should().Be("German");
    }

    [HumansTheory]
    [MemberData(nameof(SupportedCultures.Translated), MemberType = typeof(SupportedCultures))]
    public void GetDisplayName_UsesTheViewersCulture(string culture)
    {
        using var scope = new CultureScope(culture);

        LanguageCatalog.GetDisplayName("de").Should().NotBe("German");
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
