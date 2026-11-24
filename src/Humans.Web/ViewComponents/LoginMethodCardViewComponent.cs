using Humans.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.ViewComponents;

/// <summary>
/// The admin dashboard's "Sign-in method" dial: external-provider vs email-link sign-ins since the
/// process started, read from Shell's in-memory <see cref="LoginMethodCounter"/>.
/// </summary>
public sealed class LoginMethodCardViewComponent(LoginMethodCounter counter) : ViewComponent
{
    public IViewComponentResult Invoke() =>
        View(new LoginMethodCardViewModel(
            counter.Providers.OrderBy(p => p.Key, StringComparer.Ordinal).ToList(), counter.MagicLink));
}

public sealed record LoginMethodCardViewModel(IReadOnlyList<KeyValuePair<string, int>> Providers, int MagicLink)
{
    public int ProviderTotal => Providers.Sum(p => p.Value);
    public int Total => ProviderTotal + MagicLink;
    public int ProviderPercent => Total == 0 ? 0 : (int)Math.Round(100.0 * ProviderTotal / Total);
}
