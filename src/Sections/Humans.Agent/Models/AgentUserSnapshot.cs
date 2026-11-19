using Humans.Teams.Contracts;
using Humans.Users.Contracts;

namespace Humans.Agent.Models;

internal sealed record AgentUserSnapshot(
    Guid UserId,
    string DisplayName,
    string PreferredLocale,
    string Tier,
    UserState? AccountState,
    IReadOnlyList<(string RoleName, string ExpiresIsoDate)> RoleAssignments,
    IReadOnlyList<TeamMembership> Teams,
    IReadOnlyList<string> PendingConsentDocs,
    IReadOnlyList<Guid> OpenTicketIds,
    IReadOnlyList<UpcomingShiftEntry> UpcomingShifts);
