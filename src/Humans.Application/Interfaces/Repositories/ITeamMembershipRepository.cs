using Humans.Domain.Entities;

namespace Humans.Application.Interfaces.Repositories;

/// <summary>
/// Narrow read-only access to the <c>team_members</c> table. Exists to let
/// cross-section callers ask a single question — "what teams is this user
/// currently on?" — without either (a) taking a dependency on the full
/// <c>ITeamService</c> (which would close DI cycles with sync / resource
/// services) or (b) reading the table directly and violating §2c ownership.
///
/// Writes and cache coordination stay inside <c>TeamService</c>. This
/// repository is deliberately limited to cross-section read patterns that
/// the Team section's migration will later replace with a proper
/// <c>ITeamMembershipRepository</c> implementation owned by the Team section.
/// </summary>
public interface ITeamMembershipRepository
{
    /// <summary>
    /// Returns all <see cref="TeamMember"/> rows for a user where
    /// <c>LeftAt == null</c>. Read-only (AsNoTracking).
    /// </summary>
    Task<IReadOnlyList<TeamMember>> GetActiveByUserIdAsync(
        Guid userId, CancellationToken ct = default);
}
