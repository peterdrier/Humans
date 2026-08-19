using AwesomeAssertions;
using Humans.Web.Localization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Localization;
using Xunit;

namespace Humans.Web.Tests.Infrastructure;

/// <summary>
/// Coverage for the parsing-culture pin (nobodies-collective/Humans#1067): HTML forms
/// (<c>type="number"</c>, decimal model binding) always POST dot-format regardless of
/// display language, so a comma-decimal request culture silently corrupts values —
/// <c>decimal.Parse("60.00", es)</c> == 6000. Every request-culture provider is wrapped so
/// its result always reports "en" for the parsing culture, keeping only the UI culture.
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
        result!.Cultures[0].Value.Should().Be("en");
        result.UICultures[0].Value.Should().Be("es");
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
