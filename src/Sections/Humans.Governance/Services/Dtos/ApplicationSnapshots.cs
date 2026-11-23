using Humans.Governance.Domain;
using NodaTime;

using Humans.Governance.Contracts;

namespace Humans.Governance.Services.Dtos;

internal sealed record UserApplicationSnapshot(
    Guid Id,
    Guid UserId,
    ApplicationStatus Status,
    MembershipTier MembershipTier,
    Instant SubmittedAt,
    Instant? ResolvedAt,
    LocalDate? TermExpiresAt);

internal sealed record SubmittedApplicationSnapshot(
    Guid Id,
    MembershipTier MembershipTier,
    string Motivation);
