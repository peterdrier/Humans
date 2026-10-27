using NodaTime;

namespace Humans.Users.Contracts;

/// <summary>Storage only: tokens, delivery, OAuth and merge requests are the caller's.</summary>
public sealed record UserEmailAddCommand(
    string Email,
    bool IsVerified = false,
    ContactFieldVisibility? Visibility = null,
    Instant? VerificationSentAt = null,
    string? Provider = null,
    string? ProviderKey = null,
    bool IgnoreExisting = false);

public sealed record UserEmailAddResult(
    Guid EmailId,
    bool Added,
    bool IsConflict,
    string? ErrorKey = null);

public enum UserEmailPrimaryChange
{
    None,
    MakePrimary,
    ClearDuplicatePrimary,
}

/// <summary>One invariant-aware update, so the boundary doesn't grow a method per flag transition.</summary>
public sealed record UserEmailUpdateCommand(
    bool MarkVerified = false,
    UserEmailPrimaryChange Primary = UserEmailPrimaryChange.None,
    bool ChangeVisibility = false,
    ContactFieldVisibility? Visibility = null);

public enum UserEmailRemovalMode
{
    PlainEmail,
    ProviderLinkedEmail,
    AnyEmail,
}

public sealed record UserEmailRemoveCommand(
    UserEmailRemovalMode Mode,
    bool PreserveLastVerifiedEmail = true,
    bool RepairInvariants = true);

/// <summary>
/// The caller decides OAuth policy, conflicts and audit text; Users applies the rows atomically and repairs the
/// email invariants.
/// </summary>
public sealed record UserEmailReconcilePlanCommand(
    UserEmail? DisplacedRowToDelete,
    UserEmail? RowToDelete,
    UserEmail? RowToUpdate,
    UserEmail? RowToInsert);

public sealed record UserEmailReconcilePlanResult(
    IReadOnlySet<Guid> MutatedUserIds);
