using Humans.GoogleIntegration.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Humans.GoogleIntegration.ViewComponents;

/// <summary>
/// The Google parts of Users' Emails grid: the rejection banner (<c>mode="banner"</c>) and each row's Google cell
/// (<c>mode="cell"</c>). The host page authorizes the viewer; <c>WorkspaceIdentityController</c> repeats the gate.
/// </summary>
/// <remarks>Public for Razor's tag-helper discovery (HUM0034 exempts view components).</remarks>
public sealed class WorkspaceIdentityViewComponent(
    IUserService users,
    IGoogleIntegrationService identities) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(
        Guid userId, string mode = "cell", Guid? emailId = null, bool adminContext = false)
    {
        var ct = HttpContext.RequestAborted;
        var identity = await identities.GetWorkspaceIdentityAsync(userId, ct);

        if (string.Equals(mode, "banner", StringComparison.Ordinal))
            return identity?.Status == GoogleEmailStatus.Rejected ? View("Banner") : Content(string.Empty);

        var info = await users.GetUserInfoAsync(userId, ct);
        var row = info?.UserEmails.FirstOrDefault(e => e.Id == emailId);
        if (info is null || row is null)
            return Content(string.Empty);

        var isIdentity = identity is not null
            && string.Equals(identity.Email, row.Email, StringComparison.OrdinalIgnoreCase);
        // A verified @nobodies.team address is always the identity, so no other row can take it.
        var locked = !isIdentity && identity?.IsNobodiesTeamAccount == true;

        return View(new WorkspaceIdentityCellViewModel(
            info.Id, row.Id, row.IsVerified, isIdentity, locked,
            isIdentity ? identity!.Status : GoogleEmailStatus.Unknown, adminContext));
    }
}

internal sealed record WorkspaceIdentityCellViewModel(
    Guid UserId,
    Guid EmailId,
    bool IsVerified,
    bool IsIdentity,
    bool IsLocked,
    GoogleEmailStatus Status,
    bool AdminContext);
