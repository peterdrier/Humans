using Humans.Grants.Services;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace Humans.Grants.Authorization;

/// <summary>Used with a <see cref="ProgramInfo"/> as the resource.</summary>
internal sealed class ProgramOperationRequirement : IAuthorizationRequirement
{
    /// <summary>Issue, revoke, resend: a manager of this program, Admin or Board.</summary>
    public static readonly ProgramOperationRequirement Manage = new(nameof(Manage));

    /// <summary>The program itself and its managers: Admin or Board.</summary>
    public static readonly ProgramOperationRequirement Administer = new(nameof(Administer));

    private ProgramOperationRequirement(string name) => Name = name;

    public string Name { get; }
}

/// <summary>Matches manager rows against every id the viewer has held, as <see cref="GrantsPolicyHandler"/> does.</summary>
internal sealed class ProgramAuthorizationHandler(IUserService users, IHttpContextAccessor httpContextAccessor)
    : AuthorizationHandler<ProgramOperationRequirement, ProgramInfo>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, ProgramOperationRequirement requirement, ProgramInfo resource)
    {
        var ct = httpContextAccessor.HttpContext?.RequestAborted ?? CancellationToken.None;
        if (GrantsPolicyHandler.IsAdministering(context.User)
            || (requirement == ProgramOperationRequirement.Manage
                && await GrantsPolicyHandler.ViewerAsync(users, context.User, ct) is { } viewer
                && viewer.AllUserIds.Any(resource.IsManager)))
        {
            context.Succeed(requirement);
        }
    }
}
