using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Humans.Infrastructure.Data;

namespace Humans.Web.Authorization;

/// <summary>
/// Claims transformation that syncs active RoleAssignment entities to Identity role claims.
/// This enables User.IsInRole() and [Authorize(Roles = "...")] to work correctly.
/// </summary>
public class RoleAssignmentClaimsTransformation : IClaimsTransformation
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IClock _clock;

    public RoleAssignmentClaimsTransformation(IServiceProvider serviceProvider, IClock clock)
    {
        _serviceProvider = serviceProvider;
        _clock = clock;
    }

    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true)
        {
            return principal;
        }

        var userIdClaim = principal.FindFirst(ClaimTypes.NameIdentifier);
        if (userIdClaim == null || !Guid.TryParse(userIdClaim.Value, out var userId))
        {
            return principal;
        }

        // Avoid adding duplicate role claims on subsequent calls within the same request
        if (principal.HasClaim(c => string.Equals(c.Type, "RoleAssignmentClaimsAdded", StringComparison.Ordinal) && string.Equals(c.Value, "true", StringComparison.Ordinal)))
        {
            return principal;
        }

        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<HumansDbContext>();

        var now = _clock.GetCurrentInstant();

        var activeRoles = await dbContext.RoleAssignments
            .AsNoTracking()
            .Where(ra =>
                ra.UserId == userId &&
                ra.ValidFrom <= now &&
                (ra.ValidTo == null || ra.ValidTo > now))
            .Select(ra => ra.RoleName)
            .Distinct()
            .ToListAsync();

        if (activeRoles.Count == 0)
        {
            return principal;
        }

        var identity = new ClaimsIdentity();
        foreach (var role in activeRoles)
        {
            identity.AddClaim(new Claim(ClaimTypes.Role, role));
        }

        // Marker claim to prevent duplicate processing
        identity.AddClaim(new Claim("RoleAssignmentClaimsAdded", "true"));

        principal.AddIdentity(identity);

        return principal;
    }
}
