using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Profiles.Domain.Constants;
using Profiles.Infrastructure.Data;

namespace Profiles.Web.Authorization;

/// <summary>
/// Requirement for Admin role based on RoleAssignment table.
/// </summary>
public class AdminRoleRequirement : IAuthorizationRequirement
{
}

/// <summary>
/// Handler that checks if user has an active Admin RoleAssignment.
/// </summary>
public class AdminRoleHandler : AuthorizationHandler<AdminRoleRequirement>
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IClock _clock;

    public AdminRoleHandler(IServiceProvider serviceProvider, IClock clock)
    {
        _serviceProvider = serviceProvider;
        _clock = clock;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        AdminRoleRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            return;
        }

        var userIdClaim = context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier);
        if (userIdClaim == null || !Guid.TryParse(userIdClaim.Value, out var userId))
        {
            return;
        }

        // Create a scope to get the DbContext
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ProfilesDbContext>();

        var now = _clock.GetCurrentInstant();

        var hasAdminRole = await dbContext.RoleAssignments
            .AsNoTracking()
            .AnyAsync(ra =>
                ra.UserId == userId &&
                ra.RoleName == RoleNames.Admin &&
                ra.ValidFrom <= now &&
                (ra.ValidTo == null || ra.ValidTo > now));

        if (hasAdminRole)
        {
            context.Succeed(requirement);
        }
    }
}
