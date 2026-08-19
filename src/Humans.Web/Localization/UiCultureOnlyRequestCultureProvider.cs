using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Localization;

namespace Humans.Web.Localization;

/// <summary>
/// Wraps an <see cref="IRequestCultureProvider"/> so its result always reports the
/// numeric/date PARSING culture as "en", keeping only the translation (UI) culture from
/// the inner provider. HTML forms (<c>type="number"</c>, decimal model binding) always POST
/// dot-format regardless of display language, so binding under a comma-decimal request
/// culture silently corrupts values — e.g. <c>decimal.Parse("60.00", es)</c> == 6000
/// (nobodies-collective/Humans#1067). Wrapping every provider in the pipeline (cookie,
/// Accept-Language, query string, the preference-based provider) means no path can leak a
/// non-"en" parsing culture.
/// </summary>
public sealed class UiCultureOnlyRequestCultureProvider(IRequestCultureProvider inner) : IRequestCultureProvider
{
    public async Task<ProviderCultureResult?> DetermineProviderCultureResult(HttpContext httpContext)
    {
        var result = await inner.DetermineProviderCultureResult(httpContext);
        if (result is null)
            return null;

        var uiCulture = result.UICultures?.FirstOrDefault().ToString();
        return new ProviderCultureResult("en", string.IsNullOrEmpty(uiCulture) ? "en" : uiCulture);
    }
}
