namespace Humans.Application.DTOs;

/// <summary>
/// Mutually exclusive partition of users into 6 membership status categories.
/// Every user appears in exactly one bucket. Buckets sum to total input count.
/// </summary>
public record MembershipPartition(
    HashSet<Guid> IncompleteSignup,
    HashSet<Guid> PendingApproval,
    HashSet<Guid> Active,
    HashSet<Guid> MissingConsents,
    HashSet<Guid> Suspended,
    HashSet<Guid> PendingDeletion);
