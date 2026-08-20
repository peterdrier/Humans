using Humans.Users.Contracts;

namespace Humans.Users.Domain;

/// <summary>
/// Classifies <see cref="UserState"/> from the section's entities. The precedence itself lives in
/// <see cref="UserStateClassifier"/> on the leaf — this only reads the fields to feed it, and is
/// here because <see cref="Profile"/> is internal to the section.
/// </summary>
internal static class UserStateEvaluator
{
    /// <summary>Classify from entities, used by transition write-sites after they mutate fields.</summary>
    public static UserState Classify(User user, Profile? profile)
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
            isSuspended: profile?.State == ProfileState.Suspended,
            isAdminSuspended: profile?.State == ProfileState.AdminSuspended,
            isRejected: profile?.RejectedAt is not null,
            isDeletionPending: user.DeletionRequestedAt.HasValue,
            isMerged: user.MergedAt is not null && !isGdprDeleted,
            isGdprDeleted: isGdprDeleted);
    }
}
