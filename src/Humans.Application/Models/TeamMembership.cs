using Humans.Domain.Enums;

namespace Humans.Application.Models;

/// <summary>
/// A user's membership in a single team — the team's display name and the
/// role the user holds within it. Used by the agent user-context snapshot
/// and the Profile popover so coordinator-vs-member distinctions are
/// visible without re-querying team membership tables.
/// </summary>
public sealed record TeamMembership(string TeamName, TeamMemberRole RoleInTeam);
