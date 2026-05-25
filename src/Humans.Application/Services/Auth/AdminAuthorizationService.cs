using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Constants;
using NodaTime;

namespace Humans.Application.Services.Auth;

public sealed class AdminAuthorizationService(
    ICurrentUserContext currentUser,
    IRoleAssignmentRepository roleAssignments,
    IClock clock) : IAdminAuthorizationService
{
    public async Task RequireCurrentUserIsAdminAsync(CancellationToken cancellationToken = default)
    {
        var userId = currentUser.UserId
            ?? throw new UnauthorizedAccessException("An authenticated user is required.");

        var isAdmin = await roleAssignments.HasActiveRoleAsync(
            userId,
            RoleNames.Admin,
            clock.GetCurrentInstant(),
            cancellationToken);

        if (!isAdmin)
            throw new UnauthorizedAccessException("A full Admin role is required.");
    }
}
