using System.Security.Claims;
using Humans.Application.Interfaces;

namespace Humans.Application.Interfaces.Cantina;

/// <summary>
/// Authorization helper for the Cantina Daily Roster surface
/// (feature #36 — docs/features/cantina/daily-roster.md).
/// Encodes the OR-chain that gates <c>/Cantina/Roster*</c>: role short-circuit
/// (<see cref="Humans.Domain.Constants.RoleNames.Admin"/>,
/// <see cref="Humans.Domain.Constants.RoleNames.NoInfoAdmin"/>,
/// <see cref="Humans.Domain.Constants.RoleNames.VolunteerCoordinator"/>),
/// then a team-membership probe — the human is on the roster page if any
/// active team they belong to has <c>"Cantina"</c> in its name
/// (case-insensitive). Reused by the controller (HTTP 403 gate) and by the
/// nav-link visibility check (Task 7).
/// </summary>
public interface ICantinaAccessService : IApplicationService
{
    /// <summary>
    /// Returns true when <paramref name="user"/> may view the Cantina
    /// roster. Reads roles from the principal's claims (no DB hit on the
    /// common admin path); falls back to an active team-membership lookup
    /// for everyone else. Returns false when the principal has no
    /// <see cref="ClaimTypes.NameIdentifier"/> or it does not parse as a
    /// <see cref="Guid"/>.
    /// </summary>
    Task<bool> CanViewRosterAsync(ClaimsPrincipal user, CancellationToken ct = default);
}
