using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.Interfaces.Users;

/// <summary>
/// Storage-level request to add a <c>UserEmail</c> row to the Users-owned
/// <see cref="UserInfo"/> payload. Token generation, email delivery, OAuth
/// handshakes, and merge-request orchestration live outside this command.
/// </summary>
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
    bool IsConflict);

public enum UserEmailPrimaryChange
{
    None,
    MakePrimary,
    ClearDuplicatePrimary,
}

public enum UserEmailGoogleChange
{
    None,
    MakeGoogle,
    ClearDuplicateGoogle,
}

/// <summary>
/// Consolidated storage-level update for the mutable flags on a UserEmail row.
/// Commands are intentionally invariant-aware so the Users boundary does not
/// grow one public method per flag transition.
/// </summary>
public sealed record UserEmailUpdateCommand(
    bool MarkVerified = false,
    UserEmailPrimaryChange Primary = UserEmailPrimaryChange.None,
    UserEmailGoogleChange Google = UserEmailGoogleChange.None,
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
/// Storage-level OAuth reconcile mutation plan. The caller decides OAuth
/// policy, conflict handling, and audit text; Users applies the row mutation
/// atomically and repairs UserInfo-visible email invariants.
/// </summary>
public sealed record UserEmailReconcilePlanCommand(
    UserEmail? DisplacedRowToDelete,
    UserEmail? RowToDelete,
    UserEmail? RowToUpdate,
    UserEmail? RowToInsert);

public sealed record UserEmailReconcilePlanResult(
    IReadOnlySet<Guid> MutatedUserIds);
