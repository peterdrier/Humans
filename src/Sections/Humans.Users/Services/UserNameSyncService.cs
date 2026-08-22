using Humans.Users.Contracts;
using Humans.Users.Data.Repositories;

namespace Humans.Users.Services;

/// <summary>
/// nobodies-collective/Humans#1097 phase 4 — the operator-driven half of the BurnerName/legal-name
/// move. The schema migration is additive only; this is where the data actually moves, one
/// admin click at a time, and it is re-runnable.
/// </summary>
internal sealed class UserNameSyncService(
    IUserRepository repo,
    IUserService userService) : IUserNameSyncService
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
                profile.BurnerName,
                $"{profile.FirstName} {profile.LastName}".Trim(),
                burnerMissing,
                legalMissing));
        }

        return rows;
    }

    public async Task<int> SyncAllAsync(CancellationToken ct = default)
    {
        var unsynced = await GetUnsyncedAsync(ct).ConfigureAwait(false);

        var synced = 0;
        foreach (var row in unsynced)
        {
            // Re-persisting the Profile runs the same dual-write seam every profile save uses,
            // so there is exactly one place that copies names onto the User row.
            var profile = await repo.GetByUserIdAsync(row.UserId, ct).ConfigureAwait(false);
            if (profile is null)
                continue;

            await repo.UpdateAsync(profile, ct).ConfigureAwait(false);
            synced++;
        }

        return synced;
    }

    /// <summary>The User side is blank while the Profile actually carries a value.</summary>
    private static bool IsMissing(string? userValue, string profileValue) =>
        string.IsNullOrWhiteSpace(userValue) && !string.IsNullOrWhiteSpace(profileValue);
}
