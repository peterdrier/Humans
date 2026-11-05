using Humans.Base.Extensions;
using Xunit;

namespace Humans.Testing;

/// <summary>
/// <see cref="CultureCatalog.SupportedCultureCodes"/> as theory data, so adding a culture to the catalog covers it in
/// every culture-driven test without a test edit. Use with
/// <c>[MemberData(nameof(SupportedCultures.All), MemberType = typeof(SupportedCultures))]</c>.
/// </summary>
public static class SupportedCultures
{
    public static TheoryData<string> All => [.. CultureCatalog.SupportedCultureCodes];

    /// <summary>Every culture but the default (en) — the ones a resx translation must exist for.</summary>
    public static TheoryData<string> Translated =>
        [.. CultureCatalog.SupportedCultureCodes.Where(c => !string.Equals(c, CultureCatalog.DefaultCultureCode, StringComparison.Ordinal))];
}
