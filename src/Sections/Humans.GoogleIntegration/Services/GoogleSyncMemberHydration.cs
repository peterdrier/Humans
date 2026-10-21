using Humans.Base.Enums;
using Humans.Base.Helpers;
using Humans.GoogleIntegration.Contracts;
using Humans.Users.Contracts;

namespace Humans.GoogleIntegration.Services;

/// <summary>Shared eligibility and Google identity policy for membership fan-outs.</summary>
internal static class GoogleSyncMemberHydration
{
    public static async Task<Dictionary<Guid, GoogleSyncMember>> LoadAsync(
        IReadOnlyCollection<Guid> userIds,
        IUserService userService,
        IGoogleIntegrationService identityService,
        CancellationToken ct)
    {
        if (userIds.Count == 0)
            return [];

        var users = await userService.GetUserInfosAsync(userIds, ct);
        var identities = await identityService.GetWorkspaceIdentitiesAsync(userIds, ct);

        var result = new Dictionary<Guid, GoogleSyncMember>();
        foreach (var userId in userIds)
        {
            if (!users.TryGetValue(userId, out var user))
                continue;
            // A merged-away claim must not be reinterpreted as a survivor's membership.
            if (user.Id != userId)
                continue;
            if (!identities.TryGetValue(userId, out var identity))
                continue;
            if (!CanReceiveAccess(user, identity))
                continue;

            // Both Google APIs reject Gmail "+tag" grants and report the canonical
            // form. Other domains retain their exact address (#945). The stored identity
            // address is kept too, so a verdict lands on the address that was attempted.
            result[user.Id] = new GoogleSyncMember(
                user.Id, EmailNormalization.CanonicalizeGmail(identity.Email), user.BurnerName, user.ProfilePictureUrl,
                identity.Email);
        }

        return result;
    }

    internal static bool CanReceiveAccess(UserInfo user, WorkspaceIdentity identity) =>
        user.State != UserState.Rejected && !user.IsSuspended
        && !user.IsDeletionPending && identity.Status != GoogleEmailStatus.Rejected;
}

/// <summary>One member a Google fan-out grants access to. <c>Email</c> is the Gmail-canonical form
/// Google is called with; <c>IdentityEmail</c> is the member's Workspace identity address as
/// stored, which <see cref="IWorkspaceIdentityManagement.SetStatusFromSyncAsync"/> matches a
/// verdict against.</summary>
internal sealed record GoogleSyncMember(
    Guid UserId, string Email, string DisplayName, string? ProfilePictureUrl, string IdentityEmail);
