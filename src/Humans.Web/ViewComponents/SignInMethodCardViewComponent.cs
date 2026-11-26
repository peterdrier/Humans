using Humans.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.ViewComponents;

/// <summary>
/// The admin dashboard's "Sign-in method" dial: Google vs email-link sign-ins since the
/// process started, read from Shell's in-memory <see cref="SignInMethodCounter"/>.
/// </summary>
public sealed class SignInMethodCardViewComponent(SignInMethodCounter counter) : ViewComponent
{
    public IViewComponentResult Invoke() =>
        View(new SignInMethodCardViewModel(counter.Google, counter.SignInLink));
}

public sealed record SignInMethodCardViewModel(int Google, int SignInLink)
{
    public int Total => Google + SignInLink;
    public int GooglePercent => Total == 0 ? 0 : (int)Math.Round(100.0 * Google / Total);
}
