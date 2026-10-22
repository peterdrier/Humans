using Humans.Base.Interfaces;

namespace Humans.Users.Services;

/// <summary>Account/profile/ticket coverage, optionally scoped to one ticket-purchase year.</summary>
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
