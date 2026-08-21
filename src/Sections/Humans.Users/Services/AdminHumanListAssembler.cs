using Humans.Users.Contracts;

using Humans.Users.Models;

namespace Humans.Users.Services;

// Stateless helper for the admin humans-list endpoint. Caller pre-filters via searchUserIds (null when no search).
// Status buckets are derived from the canonical UserState enum; consent-aware membership partitions
// belong to the Board dashboard.
internal static class AdminHumanListAssembler
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
                u.CreatedAt.ToDateTimeUtc(),
                u.LastLoginAt?.ToDateTimeUtc(),
                u.State);
        }).ToList();
    }

    private static Func<UserInfo, bool>? FilterPredicate(string? statusFilter) =>
        statusFilter?.ToLowerInvariant() switch
        {
            "bare" => u => u.State == UserState.Bare,
            "active" => u => u.State == UserState.Active,
            "suspended" => u => u.State is UserState.Suspended or UserState.AdminSuspended,
            "adminsuspended" => u => u.State == UserState.AdminSuspended,
            "rejected" => u => u.State == UserState.Rejected,
            "deleting" or "deletepending" => u => u.State == UserState.DeletePending,
            "merged" => u => u.State == UserState.Merged,
            "deleted" => u => u.State == UserState.Deleted,
            // nobodies-collective/Humans#847: filter humans whose Google email was rejected
            // by the sync outbox processor (GoogleEmailStatus cached on UserInfo).
            "googlerejected" => u => u.GoogleEmailStatus == GoogleEmailStatus.Rejected,
            _ => null,
        };
}
