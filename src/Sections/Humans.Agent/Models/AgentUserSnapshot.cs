using Humans.Application.Models;

namespace Humans.Agent.Models;

internal sealed record AgentUserSnapshot(
    Guid UserId,
    string DisplayName,
    string PreferredLocale,
    string Tier,
    bool IsApproved,
    IReadOnlyList<(string RoleName, string ExpiresIsoDate)> RoleAssignments,
    IReadOnlyList<TeamMembership> Teams,
    IReadOnlyList<string> PendingConsentDocs,
    IReadOnlyList<Guid> OpenTicketIds,
    IReadOnlyList<Guid> OpenFeedbackIds,
    IReadOnlyList<UpcomingShiftEntry> UpcomingShifts);
