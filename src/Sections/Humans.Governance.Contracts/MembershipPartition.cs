namespace Humans.Governance.Contracts;

/// <summary>Every input user is in exactly one bucket.</summary>
public record MembershipPartition(
    HashSet<Guid> IncompleteSignup,
    HashSet<Guid> PendingApproval,
    HashSet<Guid> Active,
    HashSet<Guid> MissingConsents,
    HashSet<Guid> Suspended,
    HashSet<Guid> PendingDeletion);
