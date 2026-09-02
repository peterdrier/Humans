using Humans.Rideshare.Domain;
using Microsoft.EntityFrameworkCore;

namespace Humans.Rideshare.Data;

internal sealed class RideshareRepository(IDbContextFactory<RideshareDbContext> factory) : IRideshareRepository
{
    // ── Year graph ────────────────────────────────────────────────────────

    public async Task<RideshareYearGraph> GetYearGraphAsync(int year, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var settings = await ctx.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Year == year, ct);
        var trips = await ctx.Trips.AsNoTracking()
            .Include(t => t.Interests)
            .Where(t => t.Year == year)
            .ToListAsync(ct);
        var requests = await ctx.Requests.AsNoTracking()
            .Where(r => r.Year == year)
            .ToListAsync(ct);
        return new RideshareYearGraph(settings, trips, requests);
    }

    // ── Settings ──────────────────────────────────────────────────────────

    public async Task<RideshareSettings?> GetSettingsAsync(int year, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Year == year, ct);
    }

    public async Task UpsertSettingsAsync(RideshareSettings settings, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var existing = await ctx.Settings.FirstOrDefaultAsync(s => s.Year == settings.Year, ct);
        if (existing == null)
        {
            ctx.Settings.Add(settings);
        }
        else
        {
            existing.DestinationLabel = settings.DestinationLabel;
            existing.DestinationLatitude = settings.DestinationLatitude;
            existing.DestinationLongitude = settings.DestinationLongitude;
            existing.InboundWindowStart = settings.InboundWindowStart;
            existing.InboundWindowEnd = settings.InboundWindowEnd;
            existing.OutboundWindowStart = settings.OutboundWindowStart;
            existing.OutboundWindowEnd = settings.OutboundWindowEnd;
            existing.UpdatedAt = settings.UpdatedAt;
        }
        await ctx.SaveChangesAsync(ct);
    }

    // ── Single rows ───────────────────────────────────────────────────────

    public async Task<RideshareTrip?> GetTripAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.Trips.AsNoTracking()
            .Include(t => t.Interests)
            .FirstOrDefaultAsync(t => t.Id == id, ct);
    }

    public async Task<RideshareRequest?> GetRequestAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.Requests.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
    }

    public async Task<RideshareInterest?> GetInterestAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        // Interest → Trip → Interests is a cycle, which plain AsNoTracking refuses
        // ("Cycles are not allowed in no-tracking queries"); identity resolution
        // keeps the result detached while letting the seat maths see the siblings.
        return await ctx.Interests.AsNoTrackingWithIdentityResolution()
            .Include(i => i.Trip).ThenInclude(t => t.Interests)
            .Include(i => i.Request)
            .FirstOrDefaultAsync(i => i.Id == id, ct);
    }

    // ── Writes ────────────────────────────────────────────────────────────

    public async Task AddTripsAsync(IReadOnlyList<RideshareTrip> trips, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        ctx.Trips.AddRange(trips);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task UpdateTripAsync(RideshareTrip trip, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        ctx.Attach(trip);
        ctx.Entry(trip).State = EntityState.Modified;
        await ctx.SaveChangesAsync(ct);
    }

    public async Task AddRequestAsync(RideshareRequest request, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        ctx.Requests.Add(request);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task UpdateRequestAsync(RideshareRequest request, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        ctx.Attach(request);
        ctx.Entry(request).State = EntityState.Modified;
        await ctx.SaveChangesAsync(ct);
    }

    public async Task AddInterestAsync(RideshareInterest interest, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        ctx.Interests.Add(interest);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task UpdateInterestAsync(RideshareInterest interest, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        ctx.Attach(interest);
        ctx.Entry(interest).State = EntityState.Modified;
        await ctx.SaveChangesAsync(ct);
    }

    // ── GDPR contributor ──────────────────────────────────────────────────

    public async Task<IReadOnlyList<RideshareTrip>> GetTripsForUserAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.Trips.AsNoTracking().Where(t => t.UserId == userId).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<RideshareRequest>> GetRequestsForUserAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.Requests.AsNoTracking().Where(r => r.UserId == userId).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<RideshareInterest>> GetInterestsForUserAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.Interests.AsNoTracking().Where(i => i.FromUserId == userId).ToListAsync(ct);
    }

    public async Task DeleteUserRowsAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);

        // Tracked removes rather than ExecuteDelete so the section's EF-InMemory tests
        // exercise the same path.
        var interests = await ctx.Interests.Where(i => i.FromUserId == userId).ToListAsync(ct);
        ctx.Interests.RemoveRange(interests);

        var trips = await ctx.Trips.Include(t => t.Interests).Where(t => t.UserId == userId).ToListAsync(ct);
        ctx.Trips.RemoveRange(trips);

        var requests = await ctx.Requests.Where(r => r.UserId == userId).ToListAsync(ct);
        var requestIds = requests.Select(r => r.Id).ToList();
        // A driver's answer to one of this person's requests is a seat for this person;
        // with the request gone it would read as the driver riding their own trip, so it
        // goes too (rather than the FK's SetNull orphaning it).
        var answers = await ctx.Interests
            .Where(i => i.RequestId != null && requestIds.Contains(i.RequestId.Value))
            .ToListAsync(ct);
        ctx.Interests.RemoveRange(answers);
        ctx.Requests.RemoveRange(requests);

        await ctx.SaveChangesAsync(ct);
    }
}
