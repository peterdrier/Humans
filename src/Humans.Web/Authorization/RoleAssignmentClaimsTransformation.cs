using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NodaTime;
using Humans.Application;
using Humans.Domain.Constants;
using Humans.Infrastructure.Data;

namespace Humans.Web.Authorization;

/// <summary>
/// Claims transformation that syncs active RoleAssignment entities to Identity role claims
/// and adds membership status claims. Runs on every authenticated request.
/// Results are cached per user for 60 seconds to avoid 2 DB queries per request.
/// </summary>
public class RoleAssignmentClaimsTransformation : IClaimsTransformation
{
    /// <summary>
    /// Claim type indicating the user is an active member of the Volunteers team.
    /// </summary>
    public const string ActiveMemberClaimType = "ActiveMember";

    /// <summary>
    /// Claim type indicating the user has a profile record.
    /// Used by MembershipRequiredFilter to distinguish profileless accounts from onboarding members.
    /// </summary>
    public const string HasProfileClaimType = "HasProfile";

    public const string ActiveClaimValue = "true";
    public const string ClaimsAddedMarkerType = "RoleAssignmentClaimsAdded";

    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(60);

    private readonly IServiceProvider _serviceProvider;
    private readonly IClock _clock;
    private readonly IMemoryCache _cache;

    public RoleAssignmentClaimsTransformation(IServiceProvider serviceProvider, IClock clock, IMemoryCache cache)
    {
        _serviceProvider = serviceProvider;
        _clock = clock;
        _cache = cache;
    }

    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true)
        {
            return principal;
        }

        var userIdClaim = principal.FindFirst(ClaimTypes.NameIdentifier);
        if (userIdClaim is null || !Guid.TryParse(userIdClaim.Value, out var userId))
        {
            return principal;
        }

        // Avoid adding duplicate role claims on subsequent calls within the same request
        if (principal.HasClaim(c => string.Equals(c.Type, ClaimsAddedMarkerType, StringComparison.Ordinal) && string.Equals(c.Value, ActiveClaimValue, StringComparison.Ordinal)))
        {
            return principal;
        }

        var claims = await _cache.GetOrCreateAsync(CacheKeys.RoleAssignmentClaims(userId), async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheDuration;
            return await LoadClaimsFromDbAsync(userId);
        }) ?? [];

        var identity = new ClaimsIdentity();
        foreach (var claim in claims)
        {
            identity.AddClaim(claim);
        }

        // Marker claim to prevent duplicate processing
        identity.AddClaim(new Claim(ClaimsAddedMarkerType, ActiveClaimValue));

        principal.AddIdentity(identity);

        return principal;
    }

    private async Task<List<Claim>> LoadClaimsFromDbAsync(Guid userId)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<HumansDbContext>();

        var now = _clock.GetCurrentInstant();
        var claims = new List<Claim>();

        // Check suspension status — suspended users lose ActiveMember claim
        // but keep role claims (Admin/Board) so they can manage their own unsuspension
        var isSuspended = await dbContext.Profiles
            .AsNoTracking()
            .AnyAsync(p => p.UserId == userId && p.IsSuspended);

        var activeRoles = await dbContext.RoleAssignments
            .AsNoTracking()
            .Where(ra =>
                ra.UserId == userId &&
                ra.ValidFrom <= now &&
                (ra.ValidTo == null || ra.ValidTo > now))
            .Select(ra => ra.RoleName)
            .Distinct()
            .ToListAsync();

        foreach (var role in activeRoles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        if (!isSuspended)
        {
            var isVolunteerMember = await dbContext.TeamMembers
                .AsNoTracking()
                .AnyAsync(tm =>
                    tm.UserId == userId &&
                    tm.TeamId == SystemTeamIds.Volunteers &&
                    !tm.LeftAt.HasValue);

            if (isVolunteerMember)
            {
                claims.Add(new Claim(ActiveMemberClaimType, ActiveClaimValue));
            }
        }

        var hasProfile = await dbContext.Profiles
            .AsNoTracking()
            .AnyAsync(p => p.UserId == userId);

        if (hasProfile)
        {
            claims.Add(new Claim(HasProfileClaimType, ActiveClaimValue));
        }

        return claims;
    }
}
