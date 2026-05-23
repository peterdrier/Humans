using System.Security.Claims;
using Humans.Application.Interfaces.Cantina;
using Humans.Application.Interfaces.Teams;
using Humans.Domain.Constants;

namespace Humans.Application.Services.Cantina;

/// <summary>
/// Application-layer implementation of <see cref="ICantinaAccessService"/>.
/// Roles come from <see cref="ClaimsPrincipal.IsInRole"/> (populated by
/// <c>RoleAssignmentClaimsTransformation</c> with the 60s per-user cache),
/// so the common admin path never hits the DB. Team-membership check uses
/// <see cref="ITeamService.GetActiveTeamMembershipsForUserAsync"/> — a
/// projection that already excludes the Volunteers system team and only
/// returns active rows, so the OR-chain matches the spec without extra
/// filtering here.
/// </summary>
public sealed class CantinaAccessService : ICantinaAccessService
{
    private readonly ITeamService _teamService;

    public CantinaAccessService(ITeamService teamService)
    {
        _teamService = teamService;
    }

    public async Task<bool> CanViewRosterAsync(ClaimsPrincipal user, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        if (user.IsInRole(RoleNames.Admin)
            || user.IsInRole(RoleNames.NoInfoAdmin)
            || user.IsInRole(RoleNames.VolunteerCoordinator))
        {
            return true;
        }

        var userIdClaim = user.FindFirst(ClaimTypes.NameIdentifier);
        if (userIdClaim is null || !Guid.TryParse(userIdClaim.Value, out var userId))
        {
            return false;
        }

        var memberships = await _teamService.GetActiveTeamMembershipsForUserAsync(userId, ct).ConfigureAwait(false);
        return memberships.Any(m => m.TeamName.Contains("Cantina", StringComparison.OrdinalIgnoreCase));
    }
}
