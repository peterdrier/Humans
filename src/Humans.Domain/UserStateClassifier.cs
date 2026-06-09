using Humans.Domain.Entities;
using Humans.Domain.Enums;

namespace Humans.Domain;

/// <summary>
/// The single precedence definition for <see cref="UserState"/>. Every write-site mutates the
/// underlying fields (name, suspend, reject, deletion, merge) and then sets
/// <see cref="User.State"/> from this classifier, and the first-touch seed for legacy rows calls
/// it too, so no site can hard-code a state that drifts from the underlying data.
///
/// <para>Precedence (most-final wins): Merged &gt; Deleted &gt; Rejected &gt;
/// AdminSuspended/Suspended &gt; DeletePending &gt; Bare &gt; Active. Note GDPR deletion reuses the merge tombstone columns, so a
/// GDPR-deleted row carries <c>MergedAt</c> too: <paramref name="isMerged"/> excludes it and
/// <paramref name="isGdprDeleted"/> wins, classifying it as <see cref="UserState.Deleted"/>.</para>
///
/// <para>This is not a read-time access source; access reads the stored
/// <see cref="User.State"/>. The classifier only produces the value to persist.</para>
/// </summary>
public static class UserStateClassifier
{
    public const string GdprAnonymizedDisplayName = "Deleted User";

    public static UserState Classify(
        bool hasRequiredNameFields,
        bool isSuspended,
        bool isAdminSuspended,
        bool isRejected,
        bool isDeletionPending,
        bool isMerged,
        bool isGdprDeleted)
    {
        if (isMerged) return UserState.Merged;
        if (isGdprDeleted) return UserState.Deleted;
        if (isRejected) return UserState.Rejected;
        if (isAdminSuspended) return UserState.AdminSuspended;
        if (isSuspended) return UserState.Suspended;
        if (isDeletionPending) return UserState.DeletePending;
        if (!hasRequiredNameFields) return UserState.Bare;
        return UserState.Active;
    }

    /// <summary>Classify from entities, used by transition write-sites after they mutate fields.</summary>
    public static UserState Classify(User user, Profile? profile)
    {
        var hasName = profile is not null
            && !string.IsNullOrWhiteSpace(profile.BurnerName)
            && !string.IsNullOrWhiteSpace(profile.FirstName)
            && !string.IsNullOrWhiteSpace(profile.LastName);
        var isGdprDeleted = string.Equals(
            user.DisplayName, GdprAnonymizedDisplayName, StringComparison.Ordinal);
        return Classify(
            hasRequiredNameFields: hasName,
            isSuspended: profile?.State == ProfileState.Suspended,
            isAdminSuspended: profile?.State == ProfileState.AdminSuspended,
            isRejected: profile?.RejectedAt is not null,
            isDeletionPending: user.DeletionRequestedAt.HasValue,
            isMerged: user.MergedAt is not null && !isGdprDeleted,
            isGdprDeleted: isGdprDeleted);
    }
}
