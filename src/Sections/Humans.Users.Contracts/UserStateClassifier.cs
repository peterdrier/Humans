
namespace Humans.Users.Contracts;

/// <summary>
/// Every write site sets <see cref="User.State"/> from this, so no site can hard-code a state that drifts from the
/// data. GDPR deletion reuses the merge columns, so an erased row carries <c>MergedAt</c> too: <c>isGdprDeleted</c>
/// wins and it classifies as <see cref="UserState.Deleted"/>. Not a read-time source: access reads the stored state.
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
}
