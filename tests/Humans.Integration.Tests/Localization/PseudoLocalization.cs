using System.Globalization;
using Humans.Integration.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Localization;

namespace Humans.Integration.Tests.Localization;

/// <summary>
/// Wraps every localized lookup in <see cref="PseudoStringLocalizer.Open"/>/<see cref="PseudoStringLocalizer.Close"/>,
/// so any unbracketed prose in rendered HTML is a hard-coded literal.
/// </summary>
internal sealed class PseudoStringLocalizerFactory : IStringLocalizerFactory
{
    public IStringLocalizer Create(Type resourceSource) => PseudoStringLocalizer.Instance;

    public IStringLocalizer Create(string baseName, string location) => PseudoStringLocalizer.Instance;
}

/// <summary>
/// Returns the key itself, bracketed: the sweep only checks for markers, so this stays independent of which .resx
/// entries exist.
/// </summary>
internal sealed class PseudoStringLocalizer : IStringLocalizer
{
    /// <summary>U+27E6, which never occurs in app content.</summary>
    public const char Open = '⟦';

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
            return name;
        }
    }

    private static LocalizedString Mark(string name, string value) =>
        new(name, $"{Open}{value}{Close}", resourceNotFound: false);
}

/// <summary>
/// <see cref="HumansWebApplicationFactory"/> with <see cref="PseudoStringLocalizerFactory"/> in place of the real
/// factory, which also reaches <c>IViewLocalizer</c> and <c>IHtmlLocalizer&lt;T&gt;</c>.
/// </summary>
/// <remarks>
/// A separate app boot, but a database from the assembly's container. The sweep constructs it itself, so skipped runs
/// pay nothing.
/// </remarks>
public sealed class PseudoLocalizationWebApplicationFactory(string connectionString)
    : HumansWebApplicationFactory(connectionString)
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
