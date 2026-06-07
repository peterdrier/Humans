using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Domain.Helpers;
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
    bool IgnoreExisting = false)
{
    public string EmailForStorage => Email.Trim();

    public string NormalizedEmail => EmailNormalization.NormalizeForComparison(EmailForStorage);

    public string? AlternateNormalizedEmail
    {
        get
        {
            var normalizedEmail = NormalizedEmail;
            if (normalizedEmail.EndsWith("@gmail.com", StringComparison.Ordinal))
                return $"{normalizedEmail[..^"@gmail.com".Length]}@googlemail.com";

            if (normalizedEmail.EndsWith("@googlemail.com", StringComparison.Ordinal))
                return $"{normalizedEmail[..^"@googlemail.com".Length]}@gmail.com";

            return null;
        }
    }

    public UserEmail ToRow(Guid userId, Instant now) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        Email = EmailForStorage,
        IsVerified = IsVerified,
        IsPrimary = false,
        IsGoogle = false,
        Visibility = Visibility,
        Provider = Provider,
        ProviderKey = ProviderKey,
        VerificationSentAt = VerificationSentAt,
        CreatedAt = now,
        UpdatedAt = now,
    };
}

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
    UserEmail? RowToInsert)
{
    public IReadOnlySet<Guid> MutatedUserIds(Guid userId)
    {
        var mutatedUserIds = new HashSet<Guid> { userId };
        if (DisplacedRowToDelete is not null)
            mutatedUserIds.Add(DisplacedRowToDelete.UserId);

        return mutatedUserIds;
    }
}

public sealed record UserEmailReconcilePlanResult(
    IReadOnlySet<Guid> MutatedUserIds);
