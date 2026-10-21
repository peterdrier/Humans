using NodaTime;

namespace Humans.Governance.Contracts;

public record ApplicationDecisionResult(bool Success, string? ErrorKey = null, Guid? ApplicationId = null);

public sealed record UserApplicationSnapshot(
    Guid Id,
    Guid UserId,
    ApplicationStatus Status,
    MembershipTier MembershipTier,
    Instant SubmittedAt,
    Instant? ResolvedAt,
    LocalDate? TermExpiresAt,
    string Motivation,
    string? AdditionalInfo,
    string? SignificantContribution,
    string? RoleUnderstanding);

public sealed record ApplicationRenewalReminderCandidate(
    Guid Id,
    Guid UserId,
    MembershipTier MembershipTier,
    Instant SubmittedAt,
    LocalDate? TermExpiresAt);

public sealed record SubmittedApplicationSnapshot(
    Guid Id,
    MembershipTier MembershipTier,
    string Motivation);
