using Humans.Users.Contracts;

using Humans.Users.Models;

namespace Humans.Users.Services;

// Stateless helper for the admin humans-list endpoint. Caller pre-filters via searchUserIds (null when no search).
// Status buckets are derived from the canonical UserState enum; consent-aware membership partitions
// belong to the Board dashboard.
internal static class AdminHumanListAssembler
{
    /// <summary>The filter key for members whose Google identity sync rejected
    /// (nobodies-collective/Humans#847).</summary>
    public const string GoogleRejectedFilter = "googlerejected";

    // googleRejectedUserIds: members whose Google identity is Rejected, read from GoogleIntegration
    // by the caller when statusFilter is GoogleRejectedFilter; null (no match) otherwise.
    public static IReadOnlyList<AdminHumanRow> Assemble(
        IReadOnlyCollection<UserInfo> allUsers,
        IReadOnlyDictionary<Guid, string> notificationEmailsByUserId,
        IReadOnlySet<Guid>? searchUserIds,
        string? statusFilter,
        IReadOnlySet<Guid>? googleRejectedUserIds = null)
    {
        ArgumentNullException.ThrowIfNull(allUsers);
        ArgumentNullException.ThrowIfNull(notificationEmailsByUserId);

        IEnumerable<UserInfo> candidates = searchUserIds is null
            ? allUsers
            : allUsers.Where(u => searchUserIds.Contains(u.Id));

        var predicate = FilterPredicate(statusFilter, googleRejectedUserIds ?? new HashSet<Guid>());
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

    private static Func<UserInfo, bool>? FilterPredicate(string? statusFilter, IReadOnlySet<Guid> googleRejected) =>
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
            // nobodies-collective/Humans#847: humans whose Google identity sync rejected.
            GoogleRejectedFilter => u => googleRejected.Contains(u.Id),
            _ => null,
        };
}
