using Humans.Base.Extensions;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Governance.ViewComponents;

/// <summary>A chrome-slot contribution takes no arguments, so it reads the current user from claims.</summary>
internal sealed class GovernanceGalleryViewComponent : ViewComponent
{
    public IViewComponentResult Invoke() =>
        UserClaimsPrincipal.GetUserId() is { } currentUserId
            ? View(currentUserId)
            : Content(string.Empty);
}
