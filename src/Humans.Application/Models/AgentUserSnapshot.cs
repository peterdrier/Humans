using System.Collections.Generic;

namespace Humans.Application.Models;

public sealed record AgentUserSnapshot(
    Guid UserId,
    string DisplayName,
    string PreferredLocale,
    string Tier,
    bool IsApproved,
    IReadOnlyList<(string RoleName, string ExpiresIsoDate)> RoleAssignments,
    IReadOnlyList<string> Teams,
    IReadOnlyList<string> PendingConsentDocs,
    IReadOnlyList<Guid> OpenTicketIds,
    IReadOnlyList<Guid> OpenFeedbackIds);
