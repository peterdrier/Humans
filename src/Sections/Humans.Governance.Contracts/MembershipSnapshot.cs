namespace Humans.Governance.Contracts;

public sealed record MembershipSnapshot(
    MembershipStatus Status,
    bool IsVolunteerMember,
    int RequiredConsentCount,
    int PendingConsentCount,
    IReadOnlyList<Guid> MissingConsentVersionIds);
