using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.Primitives;

namespace Humans.Web.Localization;

/// <summary>
/// Wraps an <see cref="IRequestCultureProvider"/> so its result always reports the
/// numeric/date PARSING culture as "en", keeping the full ordered translation (UI) culture
/// candidate list from the inner provider untouched. HTML forms (<c>type="number"</c>,
/// decimal model binding) always POST dot-format regardless of display language, so binding
/// under a comma-decimal request culture silently corrupts values — e.g.
/// <c>decimal.Parse("60.00", es)</c> == 6000 (nobodies-collective/Humans#1067). Wrapping
/// every provider in the pipeline (cookie, Accept-Language, query string, the
/// preference-based provider) means no path can leak a non-"en" parsing culture.
/// <c>UICultures</c> is forwarded as-is (not collapsed to its first entry) because
/// <c>AcceptLanguageHeaderRequestCultureProvider</c> can return several ordered fallback
/// candidates in one result, and <c>RequestLocalizationMiddleware</c> walks that list to
/// find the first one that's actually supported.
/// </summary>
public sealed class UiCultureOnlyRequestCultureProvider(IRequestCultureProvider inner) : IRequestCultureProvider
{
    public async Task<ProviderCultureResult?> DetermineProviderCultureResult(HttpContext httpContext)
    {
        var result = await inner.DetermineProviderCultureResult(httpContext);
        if (result is null)
            return null;

        var uiCultures = result.UICultures is { Count: > 0 } candidates
            ? candidates
            : new List<StringSegment> { "en" };
        return new ProviderCultureResult(new List<StringSegment> { "en" }, uiCultures);
    }
}
