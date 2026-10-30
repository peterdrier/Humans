using Humans.Grants.Contracts;
using Humans.Grants.Services;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Grants.Authorization;

/// <summary>Used with a <see cref="ProgramInfo"/> as the resource.</summary>
internal sealed class ProgramOperationRequirement : IAuthorizationRequirement
{
    /// <summary>The program page: a manager of this program, Grants Admin, Admin or Board.</summary>
    public static readonly ProgramOperationRequirement View = new(nameof(View));

    /// <summary>Issue, revoke, resend: a manager of this program, Grants Admin or Admin. Never Board.</summary>
    public static readonly ProgramOperationRequirement Manage = new(nameof(Manage));

    /// <summary>The program itself and its managers: Grants Admin or Admin.</summary>
    public static readonly ProgramOperationRequirement Administer = new(nameof(Administer));

    private ProgramOperationRequirement(string name) => Name = name;

    public string Name { get; }
}

/// <summary>Matches manager rows against every id the viewer has held, as <see cref="GrantsPolicyHandler"/> does.</summary>
internal sealed class ProgramAuthorizationHandler(IUserService users)
    : AuthorizationHandler<ProgramOperationRequirement, ProgramInfo>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, ProgramOperationRequirement requirement, ProgramInfo resource)
    {
        if (GrantsRoles.AdministersGrants(context.User)
            || (requirement == ProgramOperationRequirement.View && GrantsRoles.ReadsGrants(context.User))
            || (requirement != ProgramOperationRequirement.Administer
                && await GrantsPolicyHandler.ViewerAsync(users, context.User) is { } viewer
                && viewer.AllUserIds.Any(resource.IsManager)))
        {
            context.Succeed(requirement);
        }
    }
}
