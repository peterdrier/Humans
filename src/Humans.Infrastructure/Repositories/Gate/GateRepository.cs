using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Npgsql;

namespace Humans.Infrastructure.Repositories.Gate;

/// <summary>
/// EF-backed <see cref="IGateRepository"/>. Singleton registration with a
/// short-lived <c>HumansDbContext</c> via <see cref="IDbContextFactory{TContext}"/>
/// (design-rules §15b), mirroring the other section repositories.
/// </summary>
internal sealed class GateRepository(IDbContextFactory<HumansDbContext> factory) : IGateRepository
{
    public async Task<GateScanEvent?> GetAdmitForBarcodeAsync(string admitDedupeKey, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.Set<GateScanEvent>()
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.AdmitDedupeKey == admitDedupeKey, ct);
    }

    public async Task<GateRecordOutcome> RecordScanAsync(GateScanEvent scan, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);

        // Explicit pre-check covers the common case and is the only dedupe the EF
        // in-memory provider exercises in tests; the unique index is the atomic
        // backstop for a genuine concurrent cross-lane race (caught below).
        if (scan.AdmitDedupeKey is not null &&
            await ctx.Set<GateScanEvent>().AnyAsync(e => e.AdmitDedupeKey == scan.AdmitDedupeKey, ct))
        {
            return GateRecordOutcome.DuplicateAdmitRejected;
        }

        ctx.Set<GateScanEvent>().Add(scan);
        try
        {
            await ctx.SaveChangesAsync(ct);
            return GateRecordOutcome.Recorded;
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            return GateRecordOutcome.DuplicateAdmitRejected;
        }
    }

    public async Task<IReadOnlyList<GateScanEvent>> GetScansSinceAsync(Instant since, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.Set<GateScanEvent>()
            .AsNoTracking()
            .Where(e => e.OccurredAt >= since)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<GateScanEvent>> GetScansForUserAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.Set<GateScanEvent>()
            .AsNoTracking()
            .Where(e => e.GuestUserId == userId || e.ScannedByUserId == userId)
            .ToListAsync(ct);
    }

    public async Task ReassignUserAsync(Guid fromUserId, Guid toUserId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        // Load-modify-save (not ExecuteUpdate) so the path also runs under the
        // EF in-memory test provider. At this scale the affected set is tiny.
        // Idempotent: a re-run after a partial merge failure re-points nothing.
        var rows = await ctx.Set<GateScanEvent>()
            .Where(e => e.GuestUserId == fromUserId || e.ScannedByUserId == fromUserId
                     || e.OverrideByUserId == fromUserId)
            .ToListAsync(ct);

        foreach (var row in rows)
        {
            if (row.GuestUserId == fromUserId)
                row.GuestUserId = toUserId;
            if (row.ScannedByUserId == fromUserId)
                row.ScannedByUserId = toUserId;
            if (row.OverrideByUserId == fromUserId)
                row.OverrideByUserId = toUserId;
        }

        // The survivor should keep a working device PIN: if only the merged-away
        // account had one, carry it over; otherwise the survivor keeps their own.
        var fromPin = await ctx.Set<GateStaffPin>().FirstOrDefaultAsync(p => p.UserId == fromUserId, ct);
        if (fromPin is not null)
        {
            if (!await ctx.Set<GateStaffPin>().AnyAsync(p => p.UserId == toUserId, ct))
            {
                ctx.Set<GateStaffPin>().Add(new GateStaffPin
                {
                    UserId = toUserId,
                    PinHash = fromPin.PinHash,
                    CreatedAt = fromPin.CreatedAt,
                    UpdatedAt = fromPin.UpdatedAt,
                    AdminEnrolled = fromPin.AdminEnrolled, // keep the survivor's override authority
                });
            }
            ctx.Set<GateStaffPin>().Remove(fromPin);
        }

        if (rows.Count == 0 && fromPin is null)
            return;

        await ctx.SaveChangesAsync(ct);
    }

    public async Task<int> PurgeScansBeforeAsync(Instant cutoff, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var old = await ctx.Set<GateScanEvent>()
            .Where(e => e.OccurredAt < cutoff)
            .ToListAsync(ct);
        if (old.Count == 0)
            return 0;

        ctx.Set<GateScanEvent>().RemoveRange(old);
        await ctx.SaveChangesAsync(ct);
        return old.Count;
    }

    public async Task<GateSettings> GetSettingsAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var row = await ctx.Set<GateSettings>().AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);
        return row ?? new GateSettings { Id = 1 };
    }

    public async Task SaveSettingsAsync(GateSettings settings, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var existing = await ctx.Set<GateSettings>().FirstOrDefaultAsync(s => s.Id == 1, ct);
        if (existing is null)
        {
            ctx.Set<GateSettings>().Add(new GateSettings
            {
                Id = 1,
                GeneralEntryOpensAt = settings.GeneralEntryOpensAt,
                MinorAgeThresholdYears = settings.MinorAgeThresholdYears,
            });
        }
        else
        {
            existing.GeneralEntryOpensAt = settings.GeneralEntryOpensAt;
            existing.MinorAgeThresholdYears = settings.MinorAgeThresholdYears;
        }

        await ctx.SaveChangesAsync(ct);
    }

    public async Task<GateStaffPin?> GetStaffPinAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.Set<GateStaffPin>()
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == userId, ct);
    }

    public async Task UpsertStaffPinAsync(GateStaffPin pin, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var existing = await ctx.Set<GateStaffPin>().FirstOrDefaultAsync(p => p.UserId == pin.UserId, ct);
        if (existing is null)
        {
            ctx.Set<GateStaffPin>().Add(pin);
        }
        else
        {
            existing.PinHash = pin.PinHash;
            existing.UpdatedAt = pin.UpdatedAt;
            existing.AdminEnrolled = pin.AdminEnrolled;
        }

        await ctx.SaveChangesAsync(ct);
    }

    public async Task DeleteStaffPinAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        // Load-then-RemoveRange (not ExecuteDelete) so it runs under the EF in-memory provider.
        var rows = await ctx.Set<GateStaffPin>().Where(p => p.UserId == userId).ToListAsync(ct);
        if (rows.Count == 0)
            return;
        ctx.Set<GateStaffPin>().RemoveRange(rows);
        await ctx.SaveChangesAsync(ct);
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
