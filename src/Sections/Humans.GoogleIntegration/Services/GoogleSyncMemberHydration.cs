using Humans.Base.Enums;
using Humans.Base.Helpers;
using Humans.Users.Contracts;

namespace Humans.GoogleIntegration.Services;

/// <summary>Shared eligibility and verified Google email policy for membership fan-outs.</summary>
internal static class GoogleSyncMemberHydration
{
    public static async Task<Dictionary<Guid, GoogleSyncMember>> LoadAsync(
        IReadOnlyCollection<Guid> userIds,
        IUserServiceRead userService,
        CancellationToken ct)
    {
        if (userIds.Count == 0)
            return [];

        var users = await userService.GetUserInfosAsync(userIds, ct);

        var result = new Dictionary<Guid, GoogleSyncMember>();
        foreach (var userId in userIds)
        {
            if (!users.TryGetValue(userId, out var user))
                continue;
            // A merged-away claim must not be reinterpreted as a survivor's membership.
            if (user.Id != userId)
                continue;
            if (user.GoogleEmailStatus == GoogleEmailStatus.Rejected || user.IsDeletionPending)
                continue;
            if (user.IsSuspended)
                continue;

            // The address and its status come from the same immutable user snapshot.
            var target = SelectTargetEmail(user);
            if (target is null)
                continue;

            // Both Google APIs reject Gmail "+tag" grants and report the canonical
            // form. Other domains retain their exact address (#945).
            result[user.Id] = new GoogleSyncMember(
                user.Id, EmailNormalization.CanonicalizeGmail(target.Email), user.BurnerName, user.ProfilePictureUrl, target.Id);
        }

        return result;
    }

    internal static UserEmailInfo? SelectTargetEmail(UserInfo user) =>
        user.UserEmails.FirstOrDefault(e => e.IsVerified && e.IsGoogle)
        ?? user.UserEmails.Where(e => e.IsVerified && e.Provider != null)
            .OrderBy(e => e.Email, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
}

internal sealed record GoogleSyncMember(Guid UserId, string Email, string DisplayName, string? ProfilePictureUrl, Guid GoogleEmailId);
