using Humans.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.ViewComponents;

/// <summary>
/// The admin dashboard's "Sign-in method" dial: external-provider vs email-link sign-ins since the
/// process started, read from Shell's in-memory <see cref="SignInMethodCounter"/>.
/// </summary>
public sealed class SignInMethodCardViewComponent(SignInMethodCounter counter) : ViewComponent
{
    public IViewComponentResult Invoke() =>
        View(new SignInMethodCardViewModel(
            counter.Providers.OrderBy(p => p.Key, StringComparer.Ordinal).ToList(), counter.SignInLink));
}

public sealed record SignInMethodCardViewModel(IReadOnlyList<KeyValuePair<string, int>> Providers, int SignInLink)
{
    public int ProviderTotal => Providers.Sum(p => p.Value);
    public int Total => ProviderTotal + SignInLink;
    public int ProviderPercent => Total == 0 ? 0 : (int)Math.Round(100.0 * ProviderTotal / Total);
}
