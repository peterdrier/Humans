using Humans.Application.DTOs;
using Humans.Domain.Constants;

namespace Humans.Application.Services.Profiles;

// Stateless helper for the admin humans-list endpoint. Caller pre-filters via searchUserIds (null when no search).
// Every status bucket and the cross-cutting "missing name" filter are derived purely from UserInfo flat predicates.
// The page intentionally has no Active/Missing-Consents split (consents were dropped from this view), so it no
// longer depends on IMembershipCalculator/PartitionUsersAsync — the Board dashboard still owns the consent-aware
// partition.
public static class AdminHumanListAssembler
{
    public static IReadOnlyList<AdminHumanRow> Assemble(
        IReadOnlyCollection<UserInfo> allUsers,
        IReadOnlyDictionary<Guid, string> notificationEmailsByUserId,
        IReadOnlySet<Guid>? searchUserIds,
        string? statusFilter)
    {
        ArgumentNullException.ThrowIfNull(allUsers);
        ArgumentNullException.ThrowIfNull(notificationEmailsByUserId);

        IEnumerable<UserInfo> candidates = searchUserIds is null
            ? allUsers
            : allUsers.Where(u => searchUserIds.Contains(u.Id));

        var predicate = FilterPredicate(statusFilter);
        var rows = predicate is null ? candidates : candidates.Where(predicate);

        return rows.Select(u =>
        {
            var email = notificationEmailsByUserId.TryGetValue(u.Id, out var primary)
                ? primary
                : u.Email ?? string.Empty;

            return new AdminHumanRow(
                u.Id,
                email,
                u.BurnerName,
                u.ProfilePictureUrl,
                u.CreatedAt.ToDateTimeUtc(),
                u.LastLoginAt?.ToDateTimeUtc(),
                u.HasProfile,
                u.IsApproved,
                StatusLabel(u));
        }).ToList();
    }

    // Mutually-exclusive status label, in precedence order. Tombstones first (terminal: a merged/deleted row
    // must not also read as Suspended/Pending), then the lifecycle states. Genuine no-profile or rejected rows
    // get an empty label (no badge) — they surface via the "missing name" filter, not a status bucket.
    internal static string StatusLabel(UserInfo u) =>
        u.IsMerged ? MembershipStatusLabels.Merged :
        u.IsTombstone ? MembershipStatusLabels.Deleted :
        u.IsDeletionPending ? MembershipStatusLabels.PendingDeletion :
        u.IsSuspended ? MembershipStatusLabels.Suspended :
        !u.HasProfile ? string.Empty :
        !u.IsActive ? string.Empty :
        !u.IsApproved ? MembershipStatusLabels.PendingApproval :
        MembershipStatusLabels.Active;

    // Status filters reuse StatusLabel so the filter and the badge can never drift. "hasname" is the one
    // cross-cutting filter — orthogonal to status; with every account now carrying a profile it is the
    // meaningful "active" signal (a named, usable account), which is why the UI sits it next to Active.
    private static Func<UserInfo, bool>? FilterPredicate(string? statusFilter) =>
        statusFilter?.ToLowerInvariant() switch
        {
            "active" => u => HasStatus(u, MembershipStatusLabels.Active),
            "pending" => u => HasStatus(u, MembershipStatusLabels.PendingApproval),
            "suspended" => u => HasStatus(u, MembershipStatusLabels.Suspended),
            "deleting" => u => HasStatus(u, MembershipStatusLabels.PendingDeletion),
            "merged" => u => HasStatus(u, MembershipStatusLabels.Merged),
            "deleted" => u => HasStatus(u, MembershipStatusLabels.Deleted),
            "hasname" => u => u.HasRequiredNameFields,
            _ => null,
        };

    private static bool HasStatus(UserInfo u, string label) =>
        string.Equals(StatusLabel(u), label, StringComparison.Ordinal);
}
