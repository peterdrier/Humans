using System.Security.Claims;
using Humans.Preapprovals.Services;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Preapprovals.Authorization;

/// <summary>The resource is a <see cref="ProgramInfo"/>.</summary>
internal sealed class ProgramOperationRequirement : IAuthorizationRequirement
{
    /// <summary>See the program and its grants: a manager of this program, Admin, FinanceAdmin or Board.</summary>
    public static readonly ProgramOperationRequirement View = new(nameof(View));

    /// <summary>Issue, approve, decline, revoke, change an amount: a manager of this program, Admin or FinanceAdmin.</summary>
    public static readonly ProgramOperationRequirement Manage = new(nameof(Manage));

    /// <summary>Create or edit the program itself and its managers: Admin, FinanceAdmin.</summary>
    public static readonly ProgramOperationRequirement Administer = new(nameof(Administer));

    private ProgramOperationRequirement(string name) => Name = name;

    public string Name { get; }
}

internal sealed class ProgramAuthorizationHandler(IUserService users, IHttpContextAccessor httpContextAccessor)
    : AuthorizationHandler<ProgramOperationRequirement, ProgramInfo>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, ProgramOperationRequirement requirement, ProgramInfo resource)
    {
        if (PreapprovalsPolicyHandler.IsAdministeringRole(context.User)
            || (requirement == ProgramOperationRequirement.View && PreapprovalsPolicyHandler.IsReadingRole(context.User)))
        {
            context.Succeed(requirement);
        }
        else if (requirement != ProgramOperationRequirement.Administer
            && Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)
            && await users.GetUserInfoAsync(userId,
                httpContextAccessor.HttpContext?.RequestAborted ?? CancellationToken.None) is { } viewer
            && viewer.AllUserIds.Any(resource.IsManager))
        {
            context.Succeed(requirement);
        }
    }
}
