using Humans.Domain.Enums;

namespace Humans.Application.Models;

/// <summary>
/// Lightweight projection of a user's active membership on a single team —
/// just the team name and the user's role within that team. Used by
/// <see cref="AgentUserSnapshot"/> so the agent can distinguish a coordinator
/// on Build from a regular member on Cantina without leaking the full
/// <see cref="Humans.Domain.Entities.TeamMember"/> graph into the prompt
/// surface.
/// </summary>
public sealed record TeamMembership(string TeamName, TeamMemberRole Role);
