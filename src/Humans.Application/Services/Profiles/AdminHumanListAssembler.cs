using Humans.Application.DTOs;
using Humans.Application.Interfaces.Governance;
using Humans.Domain.Constants;
using ProfileEntity = Humans.Domain.Entities.Profile;

namespace Humans.Application.Services.Profiles;

/// <summary>
/// Stateless helper used by the Admin humans-list controller endpoint to
/// assemble status-partitioned <see cref="AdminHumanRow"/> rows. Replaces
/// the deleted <c>IProfileService.GetFilteredHumansAsync</c> — orchestrating
/// "all users + profiles + partition + status filter" is presentation-layer
/// composition, not a business-logic surface that earns its own service
/// method (and the
/// <c>memory/architecture/interface-method-budget-ratchet.md</c> ratchet
/// agrees: this would have been the 40th method on
/// <c>IProfileService</c>).
///
/// <para>The text-search portion is now controller-driven: the caller may
/// pre-filter the candidate user-id set by passing
/// <paramref name="searchUserIds"/>, which is the union returned by
/// <c>IUserService.SearchUsersAsync(query, PersonSearchFields.AdminAll)</c>
/// + email-direct match. Pass <c>null</c> when no search term is in play.</para>
/// </summary>
public static class AdminHumanListAssembler
{
    public static async Task<IReadOnlyList<AdminHumanRow>> AssembleAsync(
        IReadOnlyCollection<UserInfo> allUsers,
        IReadOnlyDictionary<Guid, ProfileEntity> profilesByUserId,
        IReadOnlyDictionary<Guid, string> notificationEmailsByUserId,
        IReadOnlySet<Guid>? searchUserIds,
        string? statusFilter,
        IMembershipCalculator membershipCalculator,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(allUsers);
        ArgumentNullException.ThrowIfNull(profilesByUserId);
        ArgumentNullException.ThrowIfNull(notificationEmailsByUserId);
        ArgumentNullException.ThrowIfNull(membershipCalculator);

        IEnumerable<UserInfo> candidates = searchUserIds is null
            ? allUsers
            : allUsers.Where(u => searchUserIds.Contains(u.Id));

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

        var rows = statusIds is null
            ? candidates
            : candidates.Where(u => statusIds.Contains(u.Id));

        return rows.Select(u =>
        {
            profilesByUserId.TryGetValue(u.Id, out var profile);
            var hasProfile = profile is not null;
            var isApproved = profile?.IsApproved ?? false;

            var email = notificationEmailsByUserId.TryGetValue(u.Id, out var primary)
                ? primary
                : u.Email ?? string.Empty;

            return new AdminHumanRow(
                u.Id,
                email,
                u.DisplayName,
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
