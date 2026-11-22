using Humans.Base.Helpers;
using Humans.Google.Contracts;
using Humans.Google.Services.Workspace;

namespace Humans.Google.Services;

internal static class DrivePermissionPlanning
{
    internal static DrivePlan BuildPlan(
        IReadOnlyDictionary<Guid, DriveExpectedMember> expectedMembers,
        IReadOnlyList<DrivePermission> permissions)
    {
        // Distinct member IDs may carry aliases that Google resolves to one address.
        // Reconcile that address once at the highest level already claimed by a source.
        var byEmail = expectedMembers.Values
            .GroupBy(member => member.Email, GmailAliasEmailComparer.Instance)
            .ToDictionary(group => group.Key,
                group => group.OrderByDescending(member => member.Level)
                    .ThenBy(member => member.UserId).First(),
                GmailAliasEmailComparer.Instance);

        var allEmails = new HashSet<string>(GmailAliasEmailComparer.Instance);
        var directEmails = new HashSet<string>(GmailAliasEmailComparer.Instance);
        var roleByEmail = new Dictionary<string, string>(GmailAliasEmailComparer.Instance);
        var idByEmail = new Dictionary<string, string>(GmailAliasEmailComparer.Instance);
        var inheritedLevelByEmail = new Dictionary<string, DrivePermissionLevel>(GmailAliasEmailComparer.Instance);

        foreach (var perm in permissions)
        {
            if (!DrivePermissionRoleMapper.IsAnyUserPermission(perm))
                continue;

            var email = EmailNormalization.CanonicalizeGmail(perm.EmailAddress!);
            allEmails.Add(email);
            if (!string.IsNullOrEmpty(perm.Role))
                roleByEmail[email] = perm.Role;
            // Keep the inherited floor separate from the effective role: the latter
            // can include a direct elevation that this folder must later revoke.
            var inheritedLevels = perm.InheritedRoles.Select(DrivePermissionRoleMapper.Parse).ToArray();
            if (inheritedLevels.Length > 0 && inheritedLevels.All(level => level.HasValue))
                inheritedLevelByEmail[email] = inheritedLevels.Max()!.Value;
            if (IsDirectManagedPermission(perm) && perm.Id is not null &&
                (!perm.HasInheritedComponent || inheritedLevelByEmail.ContainsKey(email)))
            {
                directEmails.Add(email);
                idByEmail[email] = perm.Id;
            }
        }

        var members = new List<MemberSyncStatus>();
        foreach (var member in byEmail.Values)
        {
            roleByEmail.TryGetValue(member.Email, out var currentRole);
            inheritedLevelByEmail.TryGetValue(member.Email, out var inheritedLevel);
            var expectedRole = (inheritedLevel > member.Level ? inheritedLevel : member.Level).ToApiRole();

            MemberSyncState state;
            if (!allEmails.Contains(member.Email))
                state = MemberSyncState.Missing;
            else if (!directEmails.Contains(member.Email))
                // An inherited permission cannot be edited here, but a direct grant can out-rank it: a Workgroup root
                // gives every Colaborador/Asociado inherited Viewer. Below the expected level it is still Missing.
                state = DrivePermissionRoleMapper.Parse(currentRole) is { } inherited && inherited < member.Level
                    ? MemberSyncState.Missing
                    : MemberSyncState.Inherited;
            else
                state = inheritedLevel >= member.Level && DrivePermissionRoleMapper.Parse(currentRole) == inheritedLevel
                    ? MemberSyncState.Inherited
                    : string.Equals(currentRole, expectedRole, StringComparison.Ordinal)
                        ? MemberSyncState.Correct
                        : DrivePermissionRoleMapper.Parse(currentRole) > DrivePermissionRoleMapper.Parse(expectedRole)
                            ? MemberSyncState.Downgrade
                            : MemberSyncState.WrongRole;

            members.Add(new MemberSyncStatus(
                member.Email, member.DisplayName, state, [],
                currentRole, expectedRole, member.UserId, member.ProfilePictureUrl));
        }

        var extraEmails = directEmails.Where(email => !byEmail.ContainsKey(email)).ToList();
        foreach (var email in extraEmails)
        {
            roleByEmail.TryGetValue(email, out var extraRole);
            var inheritedRole = inheritedLevelByEmail.TryGetValue(email, out var inheritedLevel)
                ? inheritedLevel.ToApiRole()
                : null;
            // A mixed permission at its inherited floor grants nothing beyond the parent, and cannot be deleted here.
            if (inheritedRole is not null && DrivePermissionRoleMapper.Parse(extraRole) <= inheritedLevel)
                continue;
            members.Add(new MemberSyncStatus(email, email, MemberSyncState.Extra, [], extraRole, inheritedRole));
        }

        return new DrivePlan(members, idByEmail,
            byEmail.ToDictionary(kv => kv.Key, kv => kv.Value.IdentityEmail, GmailAliasEmailComparer.Instance));
    }

    private static bool IsDirectManagedPermission(DrivePermission perm)
    {
        if (!DrivePermissionRoleMapper.IsAnyUserPermission(perm))
            return false;
        if (string.Equals(perm.Role, "owner", StringComparison.OrdinalIgnoreCase))
            return false;

        // Mixed permissions cannot be deleted, but their direct elevation can drop to the inherited floor.
        return perm.HasDirectComponent;
    }

    internal sealed record DriveExpectedMember(
        Guid UserId, string Email, string DisplayName, string? ProfilePictureUrl, DrivePermissionLevel Level, string IdentityEmail);

    internal sealed record DrivePlan(
        List<MemberSyncStatus> Members, Dictionary<string, string> PermissionIdByEmail,
        Dictionary<string, string> IdentityEmailsByEmail);
}
