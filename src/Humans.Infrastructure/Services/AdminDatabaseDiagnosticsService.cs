using Humans.Application.Interfaces.Admin;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Tickets;
using Humans.Application.Interfaces.Users;

namespace Humans.Infrastructure.Services;

public sealed class AdminDatabaseDiagnosticsService : IAdminDatabaseDiagnosticsService
{
    private readonly IAdminDatabaseDiagnosticsRepository _repository;
    private readonly IUserService _userService;
    private readonly IProfileService _profileService;
    private readonly ITicketQueryService _ticketQueryService;

    public AdminDatabaseDiagnosticsService(
        IAdminDatabaseDiagnosticsRepository repository,
        IUserService userService,
        IProfileService profileService,
        ITicketQueryService ticketQueryService)
    {
        _repository = repository;
        _userService = userService;
        _profileService = profileService;
        _ticketQueryService = ticketQueryService;
    }

    public Task<DatabaseMigrationStatus> GetMigrationStatusAsync(CancellationToken ct = default) =>
        _repository.GetMigrationStatusAsync(ct);

    public Task<int> ClearHangfireLocksAsync(CancellationToken ct = default) =>
        _repository.ClearHangfireLocksAsync(ct);

    public async Task<AudienceSegmentation> GetAudienceSegmentationAsync(int? year, CancellationToken ct = default)
    {
        var allUsers = _userService.GetAllUserInfos();
        var allUserIds = allUsers.Select(u => u.Id).ToArray();
        var profilesByUserId = await _profileService.GetByUserIdsAsync(allUserIds, ct);
        var profileUserIds = profilesByUserId.Keys.ToHashSet();
        IReadOnlySet<Guid> ticketUserIds = year.HasValue
            ? await _ticketQueryService.GetMatchedUserIdsForYearAsync(year.Value, ct)
            : await _ticketQueryService.GetAllMatchedUserIdsAsync();

        var withProfile = 0;
        var withTicket = 0;
        var withBoth = 0;
        var withNeither = 0;

        foreach (var user in allUsers)
        {
            var hasProfile = profileUserIds.Contains(user.Id);
            var hasTicket = ticketUserIds.Contains(user.Id);

            if (hasProfile) withProfile++;
            if (hasTicket) withTicket++;
            if (hasProfile && hasTicket) withBoth++;
            if (!hasProfile && !hasTicket) withNeither++;
        }

        var years = await _ticketQueryService.GetMatchedTicketYearsAsync(ct);

        return new AudienceSegmentation(
            TotalAccounts: allUsers.Count,
            WithTicket: withTicket,
            WithProfile: withProfile,
            WithBoth: withBoth,
            WithNeither: withNeither,
            AvailableYears: years,
            SelectedYear: year);
    }
}
