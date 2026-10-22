using Humans.Base.Authorization;
using Humans.Base.Enums;
using Humans.GoogleIntegration.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.GoogleIntegration.ViewComponents;

/// <summary>
/// The admin human detail's Google Sync and @nobodies.team cards. The host page admits HumanAdmin, Board and Admin;
/// the Re-run Google Sync button stays Admin-only.
/// </summary>
/// <remarks>Public for Razor's tag-helper discovery (HUM0034 exempts view components).</remarks>
public sealed class WorkspaceIdentityCardViewComponent(
    IGoogleIntegrationService identities,
    IAuthorizationService authorization) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(Guid userId, ProfileCardViewMode viewMode)
    {
        if (viewMode != ProfileCardViewMode.Admin)
            return Content(string.Empty);

        var identity = await identities.GetWorkspaceIdentityAsync(userId, HttpContext.RequestAborted);
        var isAdmin = (await authorization.AuthorizeAsync(UserClaimsPrincipal, PolicyNames.AdminOnly)).Succeeded;
        return View(new WorkspaceIdentityCardViewModel(
            userId, identity?.Email, identity?.Status ?? GoogleEmailStatus.Unknown, isAdmin));
    }
}

internal sealed record WorkspaceIdentityCardViewModel(
    Guid UserId, string? GoogleEmail, GoogleEmailStatus Status, bool IsAdmin);
