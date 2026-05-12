using Humans.Application.Architecture;
using Humans.Application.Interfaces.Admin;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Tickets;
using Humans.Application.Interfaces.Users;
using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Humans.Infrastructure.Services;

[Grandfathered(
    ruleId: "HUM0009",
    justification: "Admin diagnostics queries currently bypass the repository layer; should be migrated to a dedicated diagnostics repository or split across existing repositories.",
    since: "2026-05-12",
    issueRef: "nobodies-collective/Humans#701")]
public sealed class AdminDatabaseDiagnosticsService : IAdminDatabaseDiagnosticsService
{
    private readonly IDbContextFactory<HumansDbContext> _factory;
    private readonly IUserService _userService;
    private readonly IProfileService _profileService;
    private readonly ITicketQueryService _ticketQueryService;

    public AdminDatabaseDiagnosticsService(
        IDbContextFactory<HumansDbContext> factory,
        IUserService userService,
        IProfileService profileService,
        ITicketQueryService ticketQueryService)
    {
        _factory = factory;
        _userService = userService;
        _profileService = profileService;
        _ticketQueryService = ticketQueryService;
    }

    public async Task<DatabaseMigrationStatus> GetMigrationStatusAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var applied = (await db.Database.GetAppliedMigrationsAsync(ct)).ToList();
        var pending = await db.Database.GetPendingMigrationsAsync(ct);

        return new DatabaseMigrationStatus(
            LastApplied: applied.LastOrDefault(),
            AppliedCount: applied.Count,
            PendingCount: pending.Count());
    }

    public async Task<int> ClearHangfireLocksAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Database.ExecuteSqlRawAsync("DELETE FROM hangfire.lock", ct);
    }

    public async Task<AudienceSegmentation> GetAudienceSegmentationAsync(int? year, CancellationToken ct = default)
    {
        var allUsers = await _userService.GetAllUsersAsync(ct);
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
