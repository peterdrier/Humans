using System.Globalization;

namespace Humans.Base.Extensions;

/// <summary>
/// Sets <see cref="CultureInfo.CurrentCulture"/> and
/// <see cref="CultureInfo.CurrentUICulture"/> for the life of the scope and restores
/// them on dispose, so a section can render one message in a recipient's culture
/// without leaking it into the rest of the request:
/// <c>using (new CultureScope(culture)) { … }</c>.
/// </summary>
/// <remarks>
/// A null, blank, or unknown culture is a no-op — the caller's culture stays in place —
/// because a bad culture code must not cost the recipient their mail.
/// </remarks>
public sealed class CultureScope : IDisposable
{
    private readonly CultureInfo? _originalCulture;
    private readonly CultureInfo? _originalUiCulture;

    public CultureScope(string? culture, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(culture)) return;

        try
        {
            var target = new CultureInfo(culture);
            _originalCulture = CultureInfo.CurrentCulture;
            _originalUiCulture = CultureInfo.CurrentUICulture;
            CultureInfo.CurrentUICulture = target;
            CultureInfo.CurrentCulture = target;
        }
        catch (CultureNotFoundException)
        {
            logger?.LogWarning("Invalid culture '{Culture}', using current culture fallback", culture);
            _originalCulture = null;
            _originalUiCulture = null;
        }
    }

    public void Dispose()
    {
        if (_originalUiCulture is null) return;

        CultureInfo.CurrentUICulture = _originalUiCulture;
        CultureInfo.CurrentCulture = _originalCulture!;
    }
}
