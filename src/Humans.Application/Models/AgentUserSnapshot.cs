using System.Collections.Generic;

namespace Humans.Application.Models;

public sealed record AgentUserSnapshot(
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
