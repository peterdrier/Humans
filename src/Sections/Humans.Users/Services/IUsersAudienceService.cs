using Humans.Base.Interfaces;

namespace Humans.Users.Services;

/// <summary>
/// Backs the Users admin area's audience-segmentation diagnostic
/// (<c>UsersAdminController.Audience</c>) — account/profile/ticket coverage, optionally
/// scoped to a single ticket-purchase year. Moved out of the deleted admin-dashboard
/// aggregator at nobodies-collective/Humans#1091; this section is its only caller, so nothing here is public.
/// </summary>
internal interface IUsersAudienceService : IApplicationService
{
    Task<AudienceSegmentation> GetAudienceSegmentationAsync(int? year, CancellationToken ct = default);
}

internal sealed record AudienceSegmentation(
    int TotalAccounts,
    int WithTicket,
    int WithProfile,
    int WithBoth,
    int WithNeither,
    IReadOnlyList<int> AvailableYears,
    int? SelectedYear);
