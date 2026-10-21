using Humans.GoogleIntegration.Contracts;
using Humans.GoogleIntegration.Services;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Humans.GoogleIntegration.ViewComponents;

/// <summary>
/// <c>&lt;vc:workspace-identity&gt;</c> — the Google parts of Users' Emails grid
/// (<c>/Profile/Me/Emails</c> and <c>/Profile/{id}/Admin/Emails</c>): the rejection banner
/// (<c>mode="banner"</c>) and, per address row, the Google cell (<c>mode="cell"</c>) with the
/// identity mark, Google's verdict and the Set / Retry form. The host page authorizes the viewer
/// (self or HumanAdmin/Board/Admin); the forms post to <c>WorkspaceIdentityController</c>, which
/// applies the same gate.
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
        var locked = !isIdentity && info.UserEmails.Any(e => e.IsVerified
            && e.Email.EndsWith(WorkspaceIdentityService.NobodiesTeamSuffix, StringComparison.OrdinalIgnoreCase));

        return View(new WorkspaceIdentityCellViewModel(
            info.Id, row.Id, row.IsVerified, isIdentity, locked,
            isIdentity ? identity!.Status : GoogleEmailStatus.Unknown, adminContext));
    }
}

/// <summary>What one Google cell of the Emails grid renders.</summary>
internal sealed record WorkspaceIdentityCellViewModel(
    Guid UserId,
    Guid EmailId,
    bool IsVerified,
    bool IsIdentity,
    bool IsLocked,
    GoogleEmailStatus Status,
    bool AdminContext);
