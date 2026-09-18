using Humans.Users.Contracts;

namespace Humans.Users.Domain;

/// <summary>
/// Classifies <see cref="UserState"/> from the section's entities. The precedence itself lives in
/// <see cref="UserStateClassifier"/> on the leaf — this only reads the fields to feed it, and is
/// here because <see cref="Profile"/> is internal to the section.
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

    /// <summary>Classify at the suspend/unsuspend transition, which supplies the new suspension.</summary>
    public static UserState Classify(User user, Profile? profile, bool isSuspended, bool isAdminSuspended)
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
            isRejected: profile?.RejectedAt is not null,
            isDeletionPending: user.DeletionRequestedAt.HasValue,
            isMerged: user.MergedAt is not null && !isGdprDeleted,
            isGdprDeleted: isGdprDeleted);
    }

    /// <summary>
    /// A row erased via GDPR Article 17. Keyed on the minted tombstone email
    /// (<c>deleted-&lt;id&gt;@deleted.local</c>, written by
    /// <see cref="Humans.Base.Interfaces.Repositories.IUserRepository.ApplyExpiredDeletionAnonymizationAsync"/>)
    /// rather than any user-editable name field — a member cannot produce that email themselves
    /// (nobodies-collective/Humans#1742: a burner/display name of "Deleted User" must not read as
    /// erased). Reads <see cref="User.IdentityEmailColumn"/> (the raw Identity column), not the
    /// <see cref="User.Email"/> override, both because the erasure path writes the tombstone there
    /// and because <c>UserEmail</c> rows are removed by the same erasure, so the raw column is the
    /// only place the tombstone can live; it is null for a live user whose address lives only in
    /// <c>UserEmail</c> rows, so a null value is never treated as erased.
    /// </summary>
    internal static bool IsGdprTombstoned(User user) =>
        (user.IdentityEmailColumn is { } email && email.EndsWith(DeletedEmailSuffix, StringComparison.OrdinalIgnoreCase))
        || IsLegacyGdprTombstone(user);

    internal const string DeletedEmailSuffix = "@deleted.local";

    /// <summary>
    /// Legacy-only: rows anonymized before the email scrub was added to the erasure path may still
    /// lack the tombstoned email, so recognise the complete pre-email tombstone shape instead — all
    /// three name columns, not the display/burner name alone. Narrower than pre-#1742 recognition on
    /// purpose; retires with the #1102 DisplayName column drop.
    /// </summary>
    private static bool IsLegacyGdprTombstone(User user) =>
        string.Equals(user.DisplayName, UserStateClassifier.GdprAnonymizedDisplayName, StringComparison.Ordinal)
        && string.Equals(user.FirstName, "Deleted", StringComparison.Ordinal)
        && string.Equals(user.LastName, "User", StringComparison.Ordinal);
}
