using Humans.Users.Contracts;
using Humans.AuditLog.Contracts;
using Humans.Users.Data.Repositories;

namespace Humans.Users.Services;

/// <summary>
/// Moves BurnerName and legal names from Profile onto User, one re-runnable admin click at a time;
/// the schema migration was additive only.
/// </summary>
internal sealed class UserNameSyncService(
    IUserRepository repo,
    IUserManagement userService,
    IAuditLogService audit) : IUserNameSyncService
{
    public async Task<IReadOnlyList<UnsyncedNameRow>> GetUnsyncedAsync(CancellationToken ct = default)
    {
        var users = await repo.GetAllAsync(ct).ConfigureAwait(false);
        var profiles = await repo.GetAllProfilesAsync(ct).ConfigureAwait(false);
        var profileByUser = profiles.ToDictionary(p => p.UserId);

        var emailByUser = (await userService.GetAllUserInfosAsync(ct).ConfigureAwait(false))
            .ToDictionary(i => i.Id, i => i.Email ?? string.Empty);

        var rows = new List<UnsyncedNameRow>();
        foreach (var user in users)
        {
            if (!profileByUser.TryGetValue(user.Id, out var profile))
                continue;

            var burnerMissing = IsMissing(user.BurnerName, profile.BurnerName);
            var legalMissing =
                IsMissing(user.FirstName, profile.FirstName)
                || IsMissing(user.LastName, profile.LastName);

            if (!burnerMissing && !legalMissing)
                continue;

            rows.Add(new UnsyncedNameRow(
                user.Id,
                emailByUser.TryGetValue(user.Id, out var email) ? email : string.Empty,
                user.State,
                profile.BurnerName,
                $"{profile.FirstName} {profile.LastName}".Trim(),
                burnerMissing,
                legalMissing));
        }

        return rows;
    }

    public async Task<int> SyncAllAsync(Guid actorUserId, CancellationToken ct = default)
    {
        var unsynced = await GetUnsyncedAsync(ct).ConfigureAwait(false);

        var synced = 0;
        foreach (var row in unsynced)
        {
            // Re-saving the Profile runs the one dual-write seam that copies names onto the User.
            var profile = await repo.GetByUserIdAsync(row.UserId, ct).ConfigureAwait(false);
            if (profile is null)
                continue;

            await repo.UpdateAsync(profile, ct).ConfigureAwait(false);
            await audit.LogAsync(AuditAction.UserNamesBackfilled, nameof(User), row.UserId,
                "Copied Profile names onto the User row.", actorUserId).ConfigureAwait(false);
            synced++;
        }

        return synced;
    }

    /// <summary>The User side is blank while the Profile actually carries a value.</summary>
    private static bool IsMissing(string? userValue, string profileValue) =>
        string.IsNullOrWhiteSpace(userValue) && !string.IsNullOrWhiteSpace(profileValue);
}
