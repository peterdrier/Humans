using Humans.Base.Data;
using Humans.Gate.Domain;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Npgsql;

namespace Humans.Gate.Data;

internal sealed class GateRepository(IDbContextFactory<GateDbContext> factory) : SectionRepository<GateDbContext>(factory), IGateRepository
{
    public async Task<GateScanEvent?> GetAdmitForBarcodeAsync(string admitDedupeKey, CancellationToken ct = default) =>
        await RunAsync(ctx => ctx.Set<GateScanEvent>()
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.AdmitDedupeKey == admitDedupeKey, ct), ct);

    public async Task<IReadOnlyList<string>> GetAdmittedKeysAsync(
        IReadOnlyCollection<string> admitDedupeKeys, CancellationToken ct = default) =>
        await RunAsync(ctx => ctx.Set<GateScanEvent>()
            .AsNoTracking()
            .Where(e => e.AdmitDedupeKey != null && admitDedupeKeys.Contains(e.AdmitDedupeKey))
            .Select(e => e.AdmitDedupeKey!)
            .ToListAsync(ct), ct);

    public async Task<GateRecordOutcome> RecordScanAsync(GateScanEvent scan, CancellationToken ct = default)
    {
        await using var ctx = await CreateDbContextAsync(ct);

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

    public async Task<GateScanEvent?> UndoAdmitAsync(
        Guid scanId, Instant undoneAt, Guid undoneByUserId, string reason, CancellationToken ct = default)
    {
        await using var ctx = await CreateDbContextAsync(ct);
        var row = await ctx.Set<GateScanEvent>().FirstOrDefaultAsync(e => e.Id == scanId, ct);
        if (row is not { IsLiveAdmit: true })
            return null;

        row.AdmitDedupeKey = null;
        row.UndoneAt = undoneAt;
        row.UndoneByUserId = undoneByUserId;
        row.UndoReason = reason;
        await ctx.SaveChangesAsync(ct);
        return row;
    }

    public async Task<IReadOnlyList<GateScanEvent>> GetScansSinceAsync(Instant since, CancellationToken ct = default) =>
        await RunAsync(ctx => ctx.Set<GateScanEvent>()
            .AsNoTracking()
            .Where(e => e.OccurredAt >= since)
            .ToListAsync(ct), ct);

    public async Task<IReadOnlyList<GateScanEvent>> GetScansForUserAsync(Guid userId, CancellationToken ct = default) =>
        await RunAsync(ctx => ctx.Set<GateScanEvent>()
            .AsNoTracking()
            .Where(e => e.GuestUserId == userId || e.ScannedByUserId == userId || e.UndoneByUserId == userId)
            .ToListAsync(ct), ct);

    public async Task<int> EraseUserFromScansAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await CreateDbContextAsync(ct);

        // Load-modify-save, not ExecuteUpdate, so it runs under the in-memory test provider.
        var rows = await ctx.Set<GateScanEvent>()
            .Where(e => e.GuestUserId == userId || e.OverrideByUserId == userId)
            .ToListAsync(ct);

        foreach (var row in rows)
        {
            if (row.GuestUserId == userId) row.GuestUserId = null;
            if (row.OverrideByUserId == userId) row.OverrideByUserId = null;
        }
        return await ctx.SaveChangesAsync(ct);
    }

    public async Task ReassignUserAsync(Guid fromUserId, Guid toUserId, CancellationToken ct = default)
    {
        await using var ctx = await CreateDbContextAsync(ct);
        // Load-modify-save, not ExecuteUpdate, so it runs under the in-memory test provider. Idempotent on re-run.
        var rows = await ctx.Set<GateScanEvent>()
            .Where(e => e.GuestUserId == fromUserId || e.ScannedByUserId == fromUserId
                     || e.OverrideByUserId == fromUserId || e.UndoneByUserId == fromUserId)
            .ToListAsync(ct);

        foreach (var row in rows)
        {
            if (row.GuestUserId == fromUserId)
                row.GuestUserId = toUserId;
            if (row.ScannedByUserId == fromUserId)
                row.ScannedByUserId = toUserId;
            if (row.OverrideByUserId == fromUserId)
                row.OverrideByUserId = toUserId;
            if (row.UndoneByUserId == fromUserId)
                row.UndoneByUserId = toUserId;
        }

        if (rows.Count == 0)
            return;

        await ctx.SaveChangesAsync(ct);
    }

    public async Task<int> PurgeScansBeforeAsync(Instant cutoff, CancellationToken ct = default)
    {
        await using var ctx = await CreateDbContextAsync(ct);
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
        await using var ctx = await CreateDbContextAsync(ct);
        var row = await ctx.Set<GateSettings>().AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);
        return row ?? new GateSettings { Id = 1 };
    }

    public async Task SaveSettingsAsync(GateSettings settings, CancellationToken ct = default)
    {
        await using var ctx = await CreateDbContextAsync(ct);
        var existing = await ctx.Set<GateSettings>().FirstOrDefaultAsync(s => s.Id == 1, ct);
        if (existing is null)
        {
            ctx.Set<GateSettings>().Add(new GateSettings
            {
                Id = 1,
                GeneralEntryOpensAt = settings.GeneralEntryOpensAt,
            });
        }
        else
        {
            existing.GeneralEntryOpensAt = settings.GeneralEntryOpensAt;
        }

        await ctx.SaveChangesAsync(ct);
    }

    private static bool IsDuplicateAdmit(DbUpdateException ex) =>
        ex.InnerException is PostgresException
        { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "ix_gate_scan_events_admit_dedupe_key" };
}
