using Humans.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.ViewComponents;

/// <summary>
/// The admin dashboard's "Sign-in method" dial: Google vs email-link sign-ins since the
/// process started, read from Shell's in-memory <see cref="LoginMethodCounter"/>.
/// </summary>
public sealed class LoginMethodCardViewComponent(LoginMethodCounter counter) : ViewComponent
{
    public IViewComponentResult Invoke() =>
        View(new LoginMethodCardViewModel(counter.Google, counter.MagicLink));
}

public sealed record LoginMethodCardViewModel(int Google, int MagicLink)
{
    public int Total => Google + MagicLink;
    public int GooglePercent => Total == 0 ? 0 : (int)Math.Round(100.0 * Google / Total);
}
