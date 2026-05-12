using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Constants;
using NodaTime;

namespace Humans.Application.Services.Auth;

public sealed class AdminAuthorizationService : IAdminAuthorizationService
{
    private readonly ICurrentUserContext _currentUser;
    private readonly IRoleAssignmentRepository _roleAssignments;
    private readonly IClock _clock;

    public AdminAuthorizationService(
        ICurrentUserContext currentUser,
        IRoleAssignmentRepository roleAssignments,
        IClock clock)
    {
        _currentUser = currentUser;
        _roleAssignments = roleAssignments;
        _clock = clock;
    }

    public async Task RequireCurrentUserIsAdminAsync(CancellationToken cancellationToken = default)
    {
        var userId = _currentUser.UserId
            ?? throw new UnauthorizedAccessException("An authenticated user is required.");

        var isAdmin = await _roleAssignments.HasActiveRoleAsync(
            userId,
            RoleNames.Admin,
            _clock.GetCurrentInstant(),
            cancellationToken);

        if (!isAdmin)
            throw new UnauthorizedAccessException("A full Admin role is required.");
    }
}
