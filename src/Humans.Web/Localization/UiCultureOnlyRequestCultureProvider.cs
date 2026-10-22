using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.Primitives;

namespace Humans.Web.Localization;

/// <summary>
/// Wraps an <see cref="IRequestCultureProvider"/> so the parsing culture is always "en" while the inner provider's
/// ordered UI-culture list passes through untouched. Forms always POST dot-format decimals, so binding under a
/// comma-decimal culture silently corrupts values: <c>decimal.Parse("60.00", es)</c> == 6000.
/// </summary>
/// <remarks>
/// <c>UICultures</c> is not collapsed to its first entry: Accept-Language can return several ordered fallbacks, and
/// <c>RequestLocalizationMiddleware</c> walks them for the first supported one.
/// </remarks>
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
