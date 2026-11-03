using Humans.Governance.Contracts;
using NodaTime;

namespace Humans.Governance.Services.Dtos;

internal sealed record ApplicationAdminRowDto(
    Guid Id,
    Guid UserId,
    string UserEmail,
    ApplicationStatus Status,
    MembershipTier MembershipTier,
    Instant SubmittedAt,
    string Motivation);
