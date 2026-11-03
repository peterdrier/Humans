using Humans.Base.Authorization;
using Humans.Finance.Contracts;
using System.Security.Claims;
using Humans.Base.Constants;
using Humans.Preapprovals.Services;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Preapprovals.Authorization;

/// <summary>Admin, Board, FinanceAdmin, or a manager of any program.</summary>
internal sealed class ManagesPreapprovalsRequirement : IAuthorizationRequirement, IDescribedRequirement
{
    public IReadOnlyList<string> Admits => [RoleNames.Admin, RoleNames.Board, FinanceRoles.FinanceAdmin, "Program managers"];
}

/// <summary><see cref="ManagesPreapprovalsRequirement"/>, or a person with a grant or an open program to apply to.</summary>
internal sealed class PreapprovalsAccessRequirement : IAuthorizationRequirement;

/// <summary>
/// The role short-circuit avoids a query on the common admin path. Both policies resolve the live user id, so a
/// merged-away id sees what the surviving account sees.
/// </summary>
internal sealed class PreapprovalsPolicyHandler(IPreapprovalManagement preapprovals) : IAuthorizationHandler
{
    public async Task HandleAsync(AuthorizationHandlerContext context)
    {
        foreach (var requirement in context.PendingRequirements.ToList())
        {
            switch (requirement)
            {
                case ManagesPreapprovalsRequirement manage when await ManagesAsync(context.User):
                    context.Succeed(manage);
                    break;
                case PreapprovalsAccessRequirement access when await ManagesAsync(context.User) || await HasAnythingAsync(context.User):
                    context.Succeed(access);
                    break;
            }
        }
    }

    /// <summary>Sees every program. Board reads and changes nothing; see <see cref="ProgramAuthorizationHandler"/>.</summary>
    internal static bool IsReadingRole(ClaimsPrincipal user) =>
        IsAdministeringRole(user) || user.IsInRole(RoleNames.Board);

    internal static bool IsAdministeringRole(ClaimsPrincipal user) =>
        user.IsInRole(RoleNames.Admin) || user.IsInRole(FinanceRoles.FinanceAdmin);

    private async Task<bool> ManagesAsync(ClaimsPrincipal user) =>
        IsReadingRole(user) || (UserId(user) is { } id && await preapprovals.ManagesAnyProgramAsync(id));

    private async Task<bool> HasAnythingAsync(ClaimsPrincipal user) =>
        UserId(user) is { } id && await preapprovals.HasAnythingForUserAsync(id);

    private static Guid? UserId(ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
}
