using Humans.Application.DTOs;
using Humans.Application.Interfaces.Governance;
using Humans.Domain.Constants;

namespace Humans.Application.Services.Profiles;

// Stateless helper for the admin humans-list endpoint. Caller pre-filters via searchUserIds (null when no search).
public static class AdminHumanListAssembler
{
    public static async Task<IReadOnlyList<AdminHumanRow>> AssembleAsync(
        IReadOnlyCollection<UserInfo> allUsers,
        IReadOnlyDictionary<Guid, string> notificationEmailsByUserId,
        IReadOnlySet<Guid>? searchUserIds,
        string? statusFilter,
        IMembershipCalculator membershipCalculator,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(allUsers);
        ArgumentNullException.ThrowIfNull(notificationEmailsByUserId);
        ArgumentNullException.ThrowIfNull(membershipCalculator);

        var candidates = (searchUserIds is null
            ? allUsers
            : allUsers.Where(u => searchUserIds.Contains(u.Id))).ToList();

        var ids = candidates.Select(u => u.Id).ToList();
        var partition = await membershipCalculator.PartitionUsersAsync(ids, ct);

        IReadOnlySet<Guid>? statusIds = statusFilter switch
        {
            _ when string.Equals(statusFilter, "active", StringComparison.OrdinalIgnoreCase) => partition.Active,
            _ when string.Equals(statusFilter, "missingconsents", StringComparison.OrdinalIgnoreCase) => partition.MissingConsents,
            _ when string.Equals(statusFilter, "pending", StringComparison.OrdinalIgnoreCase) => partition.PendingApproval,
            _ when string.Equals(statusFilter, "suspended", StringComparison.OrdinalIgnoreCase) => partition.Suspended,
            _ when string.Equals(statusFilter, "incomplete", StringComparison.OrdinalIgnoreCase) => partition.IncompleteSignup,
            _ when string.Equals(statusFilter, "deleting", StringComparison.OrdinalIgnoreCase) => partition.PendingDeletion,
            _ => null,
        };

        IEnumerable<UserInfo> rows = statusIds is null
            ? candidates
            : candidates.Where(u => statusIds.Contains(u.Id));

        return rows.Select(u =>
        {
            var hasProfile = u.Profile is not null;
            var isApproved = u.Profile?.IsApproved ?? false;

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
                hasProfile,
                isApproved,
                partition.PendingDeletion.Contains(u.Id) ? MembershipStatusLabels.PendingDeletion :
                partition.Suspended.Contains(u.Id) ? MembershipStatusLabels.Suspended :
                partition.PendingApproval.Contains(u.Id) ? MembershipStatusLabels.PendingApproval :
                partition.MissingConsents.Contains(u.Id) ? MembershipStatusLabels.MissingConsents :
                partition.Active.Contains(u.Id) ? MembershipStatusLabels.Active :
                partition.IncompleteSignup.Contains(u.Id) ? MembershipStatusLabels.IncompleteSignup :
                "Unknown");
        }).ToList();
    }
}
