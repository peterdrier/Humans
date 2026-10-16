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
        IUserEmailService userEmailService,
        CancellationToken ct)
    {
        if (userIds.Count == 0)
            return [];

        var users = await userService.GetUserInfosAsync(userIds, ct);
        var emailsByUserId = await userEmailService.GetEntitiesByUserIdsAsync(userIds, ct);

        var result = new Dictionary<Guid, GoogleSyncMember>();
        foreach (var userId in userIds)
        {
            if (!users.TryGetValue(userId, out var user))
                continue;
            // A claim on a merged-away id resolves to its survivor, but the email rows below are
            // the claimed id's own; skip rather than pair the survivor with an archived address.
            if (user.Id != userId)
                continue;
            if (user.GoogleEmailStatus == GoogleEmailStatus.Rejected || user.IsDeletionPending)
                continue;
            if (user.IsSuspended)
                continue;

            var emails = emailsByUserId.TryGetValue(userId, out var list)
                ? list
                : [];

            var email = emails
                .Where(e => e.IsVerified && e.IsGoogle)
                .Select(e => e.Email)
                .FirstOrDefault()
                ?? emails
                    .Where(e => e.IsVerified && e.Provider != null)
                    .OrderBy(e => e.Email, StringComparer.OrdinalIgnoreCase)
                    .Select(e => e.Email)
                    .FirstOrDefault();
            if (email is null)
                continue;

            // Both Google APIs reject Gmail "+tag" grants and report the canonical
            // form. Other domains retain their exact address (#945).
            result[user.Id] = new GoogleSyncMember(
                user.Id, EmailNormalization.CanonicalizeGmail(email), user.BurnerName, user.ProfilePictureUrl);
        }

        return result;
    }
}

internal sealed record GoogleSyncMember(Guid UserId, string Email, string DisplayName, string? ProfilePictureUrl);
