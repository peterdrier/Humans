using AwesomeAssertions;
using Humans.Web.Localization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.Primitives;

namespace Humans.Web.Tests.Infrastructure;

/// <summary>
/// Forms POST dot-decimals whatever the display language, so a comma-decimal parsing culture corrupts values
/// (<c>decimal.Parse("60.00", es)</c> is 6000). Providers are wrapped to keep only the UI culture.
/// </summary>
public class UiCultureOnlyRequestCultureProviderTests
{
    [HumansFact]
    public async Task Forces_parsing_culture_to_en_regardless_of_inner_result()
    {
        var inner = new FixedRequestCultureProvider(new ProviderCultureResult("es", "es"));
        var provider = new UiCultureOnlyRequestCultureProvider(inner);

        var result = await provider.DetermineProviderCultureResult(new DefaultHttpContext());

        result.Should().NotBeNull();
        result.Cultures[0].Value.Should().Be("en");
        result.UICultures[0].Value.Should().Be("es");
    }

    [HumansFact]
    public async Task Preserves_full_ui_culture_fallback_chain()
    {
        // Accept-Language can yield several candidates; the middleware walks them all, so collapsing to the first would
        // drop "es" for the default.
        var uiCultures = new List<StringSegment> { "pt-BR", "es" };
        var inner = new FixedRequestCultureProvider(
            new ProviderCultureResult(new List<StringSegment> { "en" }, uiCultures));
        var provider = new UiCultureOnlyRequestCultureProvider(inner);

        var result = await provider.DetermineProviderCultureResult(new DefaultHttpContext());

        result.Should().NotBeNull();
        result.Cultures[0].Value.Should().Be("en");
        result.UICultures.Select(c => c.Value).Should().Equal("pt-BR", "es");
    }

    [HumansFact]
    public async Task Preserves_null_when_inner_provider_has_no_opinion()
    {
        var inner = new FixedRequestCultureProvider(result: null);
        var provider = new UiCultureOnlyRequestCultureProvider(inner);

        var result = await provider.DetermineProviderCultureResult(new DefaultHttpContext());

        result.Should().BeNull();
    }

    private sealed class FixedRequestCultureProvider(ProviderCultureResult? result) : IRequestCultureProvider
    {
        public Task<ProviderCultureResult?> DetermineProviderCultureResult(HttpContext httpContext) =>
            Task.FromResult(result);
    }
}
