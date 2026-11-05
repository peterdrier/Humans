using Humans.Base.Helpers;
using Humans.Base.Interfaces;
using Humans.Users.Contracts;
using Humans.Users.Data.Repositories;
using Humans.Users.Helpers;

namespace Humans.Users.Services;

internal sealed class UserInfoReader(IUserRepository repo) : IApplicationService
{
    public async ValueTask<UserInfo?> GetUserInfoAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await repo.GetByIdAsync(userId, ct);
        if (user is null) return null;

        var userEmails = await repo.GetUserEmailsByUserIdReadOnlyAsync(userId, ct);
        var externalLoginsMap = await repo.GetExternalLoginsByUserIdsAsync([userId], ct);
        var externalLogins = externalLoginsMap.TryGetValue(userId, out var logins)
            ? logins
            : [];

        var profile = await repo.GetByUserIdReadOnlyAsync(userId, ct);
        IReadOnlyList<ContactField> contactFields = [];
        IReadOnlyList<ProfileLanguage> languages = [];
        IReadOnlyList<VolunteerHistoryEntry> volunteerHistory = [];
        if (profile is not null)
        {
            contactFields = await repo.GetByProfileIdReadOnlyAsync(profile.Id, ct);
            languages = profile.Languages.ToList();
            volunteerHistory = profile.VolunteerHistory.ToList();
        }

        var info = UserInfoFactory.Create(
            user, userEmails, externalLogins,
            profile, contactFields, languages, volunteerHistory);

        return info;
    }

    public async Task<IReadOnlyCollection<UserInfo>> GetAllUserInfosAsync(CancellationToken ct = default)
    {
        var users = await repo.GetAllAsync(ct);
        if (users.Count == 0) return [];

        var userIds = users.Select(u => u.Id).ToList();

        var allEmails = await repo.GetAllUserEmailsAsync(ct);
        var emailsByUser = allEmails
            .GroupBy(e => e.UserId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<UserEmail>)g.ToList());

        var loginsByUser = await repo.GetExternalLoginsByUserIdsAsync(userIds, ct);

        var profiles = await repo.GetAllProfilesAsync(ct);
        var profileByUser = profiles.ToDictionary(p => p.UserId);

        var allContactFields = await repo.GetAllContactFieldsAsync(ct);
        var contactFieldsByProfile = allContactFields
            .GroupBy(c => c.ProfileId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<ContactField>)g.ToList());

        var result = new List<UserInfo>(users.Count);
        foreach (var user in users)
        {
            var emails = emailsByUser.TryGetValue(user.Id, out var es) ? es : [];
            var logins = loginsByUser.TryGetValue(user.Id, out var ls) ? ls : [];
            profileByUser.TryGetValue(user.Id, out var profile);
            IReadOnlyList<ContactField> contactFields = [];
            IReadOnlyList<ProfileLanguage> languages = [];
            IReadOnlyList<VolunteerHistoryEntry> volunteerHistory = [];
            if (profile is not null)
            {
                contactFields = contactFieldsByProfile.TryGetValue(profile.Id, out var cf) ? cf : [];
                languages = profile.Languages.ToList();
                volunteerHistory = profile.VolunteerHistory.ToList();
            }

            var info = UserInfoFactory.Create(
                user, emails, logins,
                profile, contactFields, languages, volunteerHistory);
            result.Add(info);
        }

        return result;
    }

    public async ValueTask<IReadOnlyDictionary<Guid, UserInfo>> GetUserInfosAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default)
    {
        if (userIds.Count == 0)
            return new Dictionary<Guid, UserInfo>();

        var requested = userIds as IReadOnlySet<Guid> ?? new HashSet<Guid>(userIds);
        var all = await GetAllUserInfosAsync(ct);
        return all
            .Where(u => requested.Contains(u.Id))
            .ToDictionary(u => u.Id);
    }

    public async Task<UserInfo?> GetByEmailOrAlternateAsync(string email, CancellationToken ct = default)
    {
        var normalized = EmailNormalization.NormalizeForComparison(email);
        var alternate = EmailLookupAliases.GetAlternate(normalized);

        // The query needs the alternate (ILIKE matches the literal string); the in-memory re-check
        // doesn't, since EmailsMatch folds gmail/googlemail on both sides.
        var rows = await repo.GetUserEmailsByAddressAsync(normalized, alternate, ct);

        var match = rows
            .Where(r => r.IsVerified && EmailNormalization.EmailsMatch(r.Email, normalized))
            .OrderByDescending(r => r.IsPrimary)
            .FirstOrDefault();

        return match is null ? null : await GetUserInfoAsync(match.UserId, ct);
    }

}
