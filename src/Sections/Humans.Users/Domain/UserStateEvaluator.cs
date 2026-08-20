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
        var isGdprDeleted = string.Equals(
            user.DisplayName,
            UserStateClassifier.GdprAnonymizedDisplayName,
            StringComparison.Ordinal);
        return UserStateClassifier.Classify(
            hasRequiredNameFields: hasName,
            isSuspended: isSuspended,
            isAdminSuspended: isAdminSuspended,
            isRejected: profile?.RejectedAt is not null,
            isDeletionPending: user.DeletionRequestedAt.HasValue,
            isMerged: user.MergedAt is not null && !isGdprDeleted,
            isGdprDeleted: isGdprDeleted);
    }
}
