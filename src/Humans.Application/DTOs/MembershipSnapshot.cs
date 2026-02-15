using Humans.Domain.Enums;

namespace Humans.Application.DTOs;

/// <summary>
/// Consolidated membership/consent state used by web entry points.
/// </summary>
public sealed record MembershipSnapshot(
    MembershipStatus Status,
    bool IsVolunteerMember,
    int RequiredConsentCount,
    int PendingConsentCount,
    IReadOnlyList<Guid> MissingConsentVersionIds);
