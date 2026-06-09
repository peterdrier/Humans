using Humans.Domain.Helpers;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Users;

namespace Humans.Application.Services.Users;

// Detects duplicate accounts (same email across multiple User records). Resolution
// is performed by AccountMergeService.MergeAsync — this service is detection-only.
public sealed class DuplicateAccountService(
    IUserService userService,
    ITeamService teamService,
    IRoleAssignmentService roleAssignmentService) : IDuplicateAccountService
{
    public async Task<IReadOnlyList<DuplicateAccountGroup>> DetectDuplicatesAsync(CancellationToken ct = default)
    {
        // Load all into memory — ~500 users; avoids complex SQL for gmail/googlemail equivalence.
        var allInfos = await userService.GetAllUserInfosAsync(ct);
        var users = allInfos
            // Exclude tombstones (merge-archived, GDPR-anonymized, or legacy .local
            // sentinels): a merged account still carries its pre-merge legacy User.Email
            // column, so without this it re-collides with its own survivor and the
            // already-merged pair reappears on the queue forever.
            .Where(u => !string.IsNullOrEmpty(u.Email) && !u.IsTombstone)
            .ToList();

        var emailToUsers = new Dictionary<string, List<(Guid UserId, string Source)>>(StringComparer.Ordinal);

        foreach (var u in users)
        {
            var normalized = EmailNormalization.NormalizeForComparison(u.Email!);
            if (!emailToUsers.TryGetValue(normalized, out var list))
            {
                list = [];
                emailToUsers[normalized] = list;
            }
            list.Add((u.Id, $"User.Email ({u.Email})"));

            foreach (var ue in u.UserEmails)
            {
                var ueNormalized = EmailNormalization.NormalizeForComparison(ue.Email);
                if (!emailToUsers.TryGetValue(ueNormalized, out var ueList))
                {
                    ueList = [];
                    emailToUsers[ueNormalized] = ueList;
                }
                var verifiedTag = ue.IsVerified ? "verified" : "unverified";
                var googleTag = ue.IsGoogle ? ", Google" : "";
                ueList.Add((u.Id, $"UserEmail ({ue.Email}, {verifiedTag}{googleTag})"));
            }
        }

        var conflicts = emailToUsers
            .Where(kvp => kvp.Value.Select(x => x.UserId).Distinct().Count() > 1)
            .ToList();

        if (conflicts.Count == 0)
            return [];

        var pairGroups = new Dictionary<string, DuplicateAccountGroup>(StringComparer.Ordinal);
        var involvedUserIds = conflicts
            .SelectMany(c => c.Value.Select(x => x.UserId))
            .Distinct()
            .ToList();
        var involvedUserSet = involvedUserIds.ToHashSet();

        var infoMap = users.Where(u => involvedUserSet.Contains(u.Id))
            .ToDictionary(u => u.Id);

        // Per-user team counts (active only).
        var teamCounts = new Dictionary<Guid, int>();
        var roleAssignmentCounts = new Dictionary<Guid, int>();
        foreach (var userId in involvedUserIds)
        {
            var memberships = await teamService.GetUserTeamsAsync(userId, ct);
            teamCounts[userId] = memberships.Count;

            var roles = await roleAssignmentService.GetByUserIdAsync(userId, ct);
            roleAssignmentCounts[userId] = roles.Count(r => r.ValidTo == null);
        }

        foreach (var (normalizedEmail, entries) in conflicts)
        {
            var distinctUserIds = entries.Select(x => x.UserId).Distinct().ToList();

            for (var i = 0; i < distinctUserIds.Count; i++)
            {
                for (var j = i + 1; j < distinctUserIds.Count; j++)
                {
                    var id1 = distinctUserIds[i];
                    var id2 = distinctUserIds[j];
                    var pairKey = string.Compare(id1.ToString(), id2.ToString(), StringComparison.Ordinal) < 0
                        ? $"{id1}:{id2}"
                        : $"{id2}:{id1}";

                    if (pairGroups.ContainsKey(pairKey))
                        continue;

                    // Raw email for display from first source.
                    var firstSource = entries.First().Source;
                    var emailStart = firstSource.IndexOf('(');
                    var emailEnd = firstSource.IndexOfAny([',', ')'], emailStart + 1);
                    var rawEmail = emailStart >= 0 && emailEnd > emailStart
                        ? firstSource[(emailStart + 1)..emailEnd]
                        : normalizedEmail;

                    pairGroups[pairKey] = new DuplicateAccountGroup
                    {
                        SharedEmail = rawEmail,
                        Accounts =
                        [
                            BuildAccountInfo(id1,
                                entries.Where(e => e.UserId == id1).Select(e => e.Source).ToList(),
                                infoMap, teamCounts, roleAssignmentCounts),
                            BuildAccountInfo(id2,
                                entries.Where(e => e.UserId == id2).Select(e => e.Source).ToList(),
                                infoMap, teamCounts, roleAssignmentCounts)
                        ]
                    };
                }
            }
        }

        return pairGroups.Values.ToList();
    }

    public async Task<DuplicateAccountGroup?> GetDuplicateGroupAsync(
        Guid userId1, Guid userId2, CancellationToken ct = default)
    {
        var groups = await DetectDuplicatesAsync(ct);
        return groups.FirstOrDefault(g =>
            g.Accounts.Any(a => a.UserId == userId1) &&
            g.Accounts.Any(a => a.UserId == userId2));
    }

    private static DuplicateAccountInfo BuildAccountInfo(
        Guid userId,
        List<string> emailSources,
        Dictionary<Guid, UserInfo> infoMap,
        Dictionary<Guid, int> teamCounts,
        Dictionary<Guid, int> roleAssignmentCounts)
    {
        infoMap.TryGetValue(userId, out var info);
        var profile = info?.Profile;

        string? membershipStatus = null;
        string? membershipTier = null;
        var hasProfile = profile is not null;
        var isProfileComplete = false;

        if (info is not null && profile is not null)
        {
            membershipTier = profile.MembershipTier.ToString();
            membershipStatus = info.IsSuspended ? "Suspended"
                : profile.IsApproved ? "Active" : "Pending";
            isProfileComplete = !string.IsNullOrEmpty(profile.FirstName) &&
                                !string.IsNullOrEmpty(profile.LastName);
        }

        return new DuplicateAccountInfo
        {
            UserId = userId,
            DisplayName = info?.BurnerName ?? "Unknown",
            Email = info?.Email,
            ProfilePictureUrl = info?.ProfilePictureUrl,
            MembershipTier = membershipTier,
            MembershipStatus = membershipStatus,
            LastLogin = info?.LastLoginAt?.ToDateTimeUtc(),
            CreatedAt = info?.CreatedAt.ToDateTimeUtc(),
            TeamCount = teamCounts.GetValueOrDefault(userId),
            RoleAssignmentCount = roleAssignmentCounts.GetValueOrDefault(userId),
            HasProfile = hasProfile,
            IsProfileComplete = isProfileComplete,
            EmailSources = emailSources
        };
    }
}
