using Humans.Application.Interfaces.Admin;
using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Humans.Infrastructure.Services;

public sealed class AdminDatabaseDiagnosticsService : IAdminDatabaseDiagnosticsService
{
    private readonly IDbContextFactory<HumansDbContext> _factory;

    public AdminDatabaseDiagnosticsService(IDbContextFactory<HumansDbContext> factory)
    {
        _factory = factory;
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
        await using var db = await _factory.CreateDbContextAsync(ct);

        var allUserIds = await db.Users
            .Select(u => u.Id)
            .ToListAsync(ct);

        var profileUserIds = await db.Profiles
            .Select(p => p.UserId)
            .ToHashSetAsync(ct);

        HashSet<Guid> ticketUserIds;
        if (year.HasValue)
        {
            var yearStart = new DateTime(year.Value, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var yearEnd = new DateTime(year.Value + 1, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var startInstant = NodaTime.Instant.FromDateTimeUtc(yearStart);
            var endInstant = NodaTime.Instant.FromDateTimeUtc(yearEnd);

            var orderUserIds = await db.TicketOrders
                .Where(o => o.MatchedUserId != null &&
                            o.PurchasedAt >= startInstant &&
                            o.PurchasedAt < endInstant)
                .Select(o => o.MatchedUserId!.Value)
                .Distinct()
                .ToListAsync(ct);

            var attendeeUserIds = await db.TicketAttendees
                .Where(a => a.MatchedUserId != null &&
                            a.TicketOrder.PurchasedAt >= startInstant &&
                            a.TicketOrder.PurchasedAt < endInstant)
                .Select(a => a.MatchedUserId!.Value)
                .Distinct()
                .ToListAsync(ct);

            ticketUserIds = orderUserIds.Concat(attendeeUserIds).ToHashSet();
        }
        else
        {
            var orderUserIds = await db.TicketOrders
                .Where(o => o.MatchedUserId != null)
                .Select(o => o.MatchedUserId!.Value)
                .Distinct()
                .ToListAsync(ct);

            var attendeeUserIds = await db.TicketAttendees
                .Where(a => a.MatchedUserId != null)
                .Select(a => a.MatchedUserId!.Value)
                .Distinct()
                .ToListAsync(ct);

            ticketUserIds = orderUserIds.Concat(attendeeUserIds).ToHashSet();
        }

        var totalAccounts = allUserIds.Count;
        var withProfile = 0;
        var withTicket = 0;
        var withBoth = 0;
        var withNeither = 0;

        foreach (var userId in allUserIds)
        {
            var hasProfile = profileUserIds.Contains(userId);
            var hasTicket = ticketUserIds.Contains(userId);

            if (hasProfile) withProfile++;
            if (hasTicket) withTicket++;
            if (hasProfile && hasTicket) withBoth++;
            if (!hasProfile && !hasTicket) withNeither++;
        }

        var availableYears = await db.TicketOrders
            .Where(o => o.MatchedUserId != null)
            .Select(o => o.PurchasedAt)
            .Distinct()
            .ToListAsync(ct);

        var years = availableYears
            .Select(i => i.ToDateTimeUtc().Year)
            .Distinct()
            .OrderDescending()
            .ToList();

        return new AudienceSegmentation(
            TotalAccounts: totalAccounts,
            WithTicket: withTicket,
            WithProfile: withProfile,
            WithBoth: withBoth,
            WithNeither: withNeither,
            AvailableYears: years,
            SelectedYear: year);
    }
}
