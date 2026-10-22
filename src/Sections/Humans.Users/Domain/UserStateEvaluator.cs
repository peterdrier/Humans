using Humans.Users.Contracts;

namespace Humans.Users.Domain;

/// <summary>
/// Feeds <see cref="UserStateClassifier"/> from the section's entities; lives here because
/// <see cref="Profile"/> is internal.
/// </summary>
internal static class UserStateEvaluator
{
    /// <summary>
    /// Classify after a non-suspension mutation. Suspension is stored on <see cref="User.State"/>,
    /// so it is carried forward from the value already on the row.
    /// </summary>
    public static UserState Classify(User user, Profile? profile) =>
        Classify(
            user,
            profile,
            isSuspended: user.State == UserState.Suspended,
            isAdminSuspended: user.State == UserState.AdminSuspended);

    /// <summary>
    /// Classify at the suspend/unsuspend or reject transition, which supplies the new signal.
    /// A rejection is carried forward from the stored state (<c>IUserRepository.SetRejectedAsync</c>
    /// writes it; Consent keeps the record) or read from the frozen legacy <c>profiles.RejectedAt</c>.
    /// </summary>
    public static UserState Classify(
        User user, Profile? profile, bool isSuspended, bool isAdminSuspended, bool isRejected = false)
    {
        var hasName = profile is not null
            && !string.IsNullOrWhiteSpace(profile.BurnerName)
            && !string.IsNullOrWhiteSpace(profile.FirstName)
            && !string.IsNullOrWhiteSpace(profile.LastName);
        var isGdprDeleted = IsGdprTombstoned(user);
        return UserStateClassifier.Classify(
            hasRequiredNameFields: hasName,
            isSuspended: isSuspended,
            isAdminSuspended: isAdminSuspended,
            isRejected: isRejected || user.State == UserState.Rejected || profile?.RejectedAt is not null,
            isDeletionPending: user.DeletionRequestedAt.HasValue,
            isMerged: user.MergedAt is not null && !isGdprDeleted,
            isGdprDeleted: isGdprDeleted);
    }

    /// <summary>
    /// A row erased via GDPR Article 17, keyed on the minted tombstone email rather than a name a
    /// member could type. Reads <see cref="User.IdentityEmailColumn"/>: erasure removes the
    /// UserEmail rows, so the raw column is the only place the tombstone lives (null never means erased).
    /// </summary>
    internal static bool IsGdprTombstoned(User user) =>
        (user.IdentityEmailColumn is { } email && email.EndsWith(DeletedEmailSuffix, StringComparison.OrdinalIgnoreCase))
        || IsLegacyGdprTombstone(user);

    /// <summary>Terminal accounts must not acquire new personal data or credentials.</summary>
    internal static bool IsTombstone(User user) =>
        user.State is UserState.Deleted or UserState.Merged
        || user.MergedAt is not null
        || user.MergedToUserId is not null
        || IsGdprTombstoned(user)
        || (user.IdentityEmailColumn is { } email
            && email.EndsWith("@merged.local", StringComparison.OrdinalIgnoreCase));

    internal const string DeletedEmailSuffix = "@deleted.local";

    /// <summary>
    /// Legacy-only: rows anonymized before erasure scrubbed the email. Safe only because erasure
    /// blanked BurnerName, which a member typing "Deleted User" never does. Delete once no pre-scrub
    /// erased rows survive in production, keying erasure solely on the minted email.
    /// </summary>
    private static bool IsLegacyGdprTombstone(User user) =>
        string.IsNullOrWhiteSpace(user.BurnerName)
        && string.Equals(user.DisplayName, UserStateClassifier.GdprAnonymizedDisplayName, StringComparison.Ordinal)
        && string.Equals(user.FirstName, "Deleted", StringComparison.Ordinal)
        && string.Equals(user.LastName, "User", StringComparison.Ordinal);
}
