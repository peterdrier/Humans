using Humans.Gate.Domain;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Npgsql;

namespace Humans.Gate.Data;

internal sealed class GateRepository(IDbContextFactory<GateDbContext> factory) : IGateRepository
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

        // The pre-check is the only dedupe the in-memory test provider exercises; the unique index is the atomic
        // backstop for a concurrent cross-lane race (caught below).
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
        catch (DbUpdateException ex) when (IsDuplicateAdmit(ex))
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

    public async Task<int> EraseUserFromScansAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);

        // Load-modify-save, not ExecuteUpdate, so it runs under the in-memory test provider.
        var rows = await ctx.Set<GateScanEvent>()
            .Where(e => e.GuestUserId == userId || e.OverrideByUserId == userId)
            .ToListAsync(ct);

        foreach (var row in rows)
        {
            if (row.GuestUserId == userId) row.GuestUserId = null;
            if (row.OverrideByUserId == userId) row.OverrideByUserId = null;
        }

        var pins = await ctx.Set<GateStaffPin>().Where(p => p.UserId == userId).ToListAsync(ct);
        ctx.Set<GateStaffPin>().RemoveRange(pins);

        return await ctx.SaveChangesAsync(ct);
    }

    public async Task ReassignUserAsync(Guid fromUserId, Guid toUserId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        // Load-modify-save, not ExecuteUpdate, so it runs under the in-memory test provider. Idempotent on re-run.
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

        // Carry the merged-away PIN over only when the survivor has none.
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
                    AdminEnrolled = fromPin.AdminEnrolled,
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
        // Load-then-RemoveRange, not ExecuteDelete, so it runs under the in-memory provider.
        var rows = await ctx.Set<GateStaffPin>().Where(p => p.UserId == userId).ToListAsync(ct);
        if (rows.Count == 0)
            return;
        ctx.Set<GateStaffPin>().RemoveRange(rows);
        await ctx.SaveChangesAsync(ct);
    }

    private static bool IsDuplicateAdmit(DbUpdateException ex) =>
        ex.InnerException is PostgresException
        { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "ix_gate_scan_events_admit_dedupe_key" };
}
