using Humans.Governance.Contracts;
using NodaTime;

namespace Humans.Governance.Services.Dtos;

internal sealed record ApplicationDecisionResult(bool Success, string? ErrorKey = null, Guid? ApplicationId = null);

internal sealed record ApplicationRenewalReminderCandidate(
    Guid Id,
    Guid UserId,
    MembershipTier MembershipTier,
    Instant SubmittedAt,
    LocalDate? TermExpiresAt);

internal sealed record ApplicationAdminStats(
    int Total,
    int Approved,
    int Rejected,
    int ColaboradorApplied,
    int AsociadoApplied);
