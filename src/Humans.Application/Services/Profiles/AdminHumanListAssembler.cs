using Humans.Application.DTOs;
using Humans.Domain.Enums;

namespace Humans.Application.Services.Profiles;

// Stateless helper for the admin humans-list endpoint. Caller pre-filters via searchUserIds (null when no search).
// Status buckets are derived from the canonical UserState enum; consent-aware membership partitions
// belong to the Board dashboard.
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
                StateOf(u));
        }).ToList();
    }

    private static UserState StateOf(UserInfo u) => u.State ?? UserState.Bare;

    private static Func<UserInfo, bool>? FilterPredicate(string? statusFilter) =>
        statusFilter?.ToLowerInvariant() switch
        {
            "bare" => u => StateOf(u) == UserState.Bare,
            "active" => u => StateOf(u) == UserState.Active,
            "suspended" => u => StateOf(u) is UserState.Suspended or UserState.AdminSuspended,
            "adminsuspended" => u => StateOf(u) == UserState.AdminSuspended,
            "rejected" => u => StateOf(u) == UserState.Rejected,
            "deleting" or "deletepending" => u => StateOf(u) == UserState.DeletePending,
            "merged" => u => StateOf(u) == UserState.Merged,
            "deleted" => u => StateOf(u) == UserState.Deleted,
            _ => null,
        };
}
