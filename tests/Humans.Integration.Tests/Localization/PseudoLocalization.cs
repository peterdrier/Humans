using System.Globalization;
using Humans.Integration.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Localization;

namespace Humans.Integration.Tests.Localization;

/// <summary>
/// Test-only <see cref="IStringLocalizerFactory"/> that wraps every localized lookup in
/// sentinel brackets (<see cref="PseudoStringLocalizer.Open"/> / <see cref="PseudoStringLocalizer.Close"/>).
/// When the app renders through this factory, any user-visible text that came from the
/// localizer is bracketed; therefore any <em>unbracketed</em> prose in the rendered HTML is a
/// hard-coded (non-localized) literal. This is the engine of the localization-coverage sweep.
/// </summary>
internal sealed class PseudoStringLocalizerFactory : IStringLocalizerFactory
{
    public IStringLocalizer Create(Type resourceSource) => PseudoStringLocalizer.Instance;

    public IStringLocalizer Create(string baseName, string location) => PseudoStringLocalizer.Instance;
}

/// <summary>
/// Returns every requested key wrapped in marker brackets. The sweep only cares whether a
/// rendered run is marked, so the real resource values are irrelevant — keying off the name
/// keeps this independent of which .resx entries happen to exist.
/// </summary>
internal sealed class PseudoStringLocalizer : IStringLocalizer
{
    /// <summary>U+27E6 MATHEMATICAL LEFT WHITE SQUARE BRACKET — does not occur in app content.</summary>
    public const char Open = '⟦';

    /// <summary>U+27E7 MATHEMATICAL RIGHT WHITE SQUARE BRACKET.</summary>
    public const char Close = '⟧';

    public static readonly PseudoStringLocalizer Instance = new();

    public LocalizedString this[string name] => Mark(name, name);

    public LocalizedString this[string name, params object[] arguments] =>
        Mark(name, SafeFormat(name, arguments));

    // The sweep never enumerates resources; it only inspects rendered output for markers.
    public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];

    private static string SafeFormat(string name, object[] arguments)
    {
        if (arguments is not { Length: > 0 })
            return name;
        try
        {
            return string.Format(CultureInfo.CurrentCulture, name, arguments);
        }
        catch (FormatException)
        {
            // Key is a plain resource name (not a composite format string) — leave it as-is.
            return name;
        }
    }

    private static LocalizedString Mark(string name, string value) =>
        new(name, $"{Open}{value}{Close}", resourceNotFound: false);
}

/// <summary>
/// <see cref="HumansWebApplicationFactory"/> with the real <see cref="IStringLocalizerFactory"/>
/// swapped for <see cref="PseudoStringLocalizerFactory"/>, so every localized string renders
/// bracketed. The swap also reaches <c>IViewLocalizer</c> and <c>IHtmlLocalizer&lt;T&gt;</c>,
/// which the framework builds on top of <see cref="IStringLocalizerFactory"/>. Isolated to the
/// sweep fixture — all other integration tests keep the real localizer.
/// </summary>
public sealed class PseudoLocalizationWebApplicationFactory : HumansWebApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IStringLocalizerFactory>();
            services.AddSingleton<IStringLocalizerFactory, PseudoStringLocalizerFactory>();
        });
    }
}
