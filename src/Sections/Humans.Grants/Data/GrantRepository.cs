using Humans.Grants.Domain;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Humans.Grants.Data;

internal sealed class GrantRepository(IDbContextFactory<GrantsDbContext> factory) : IGrantRepository
{
    internal const string MergeRevokeReason = "Duplicate after account merge";

    public async Task<IReadOnlyList<GrantProgram>> GetProgramsAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ProgramsWithChildren(ctx).ToListAsync(ct);
    }

    public async Task<GrantProgram?> GetProgramAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ProgramsWithChildren(ctx).FirstOrDefaultAsync(p => p.Id == id, ct);
    }

    public async Task<Grant?> GetGrantAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.Grants.AsNoTrackingWithIdentityResolution()
            .Include(g => g.Program).ThenInclude(p => p.Managers)
            .FirstOrDefaultAsync(g => g.Id == id, ct);
    }

    public async Task<IReadOnlyList<Grant>> GetGrantsForUsersAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default)
    {
        if (userIds.Count == 0) return [];
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.Grants.AsNoTracking()
            .Include(g => g.Program)
            .Where(g => userIds.Contains(g.UserId))
            .ToListAsync(ct);
    }

    public async Task<bool> ManagesAnyProgramAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct = default)
    {
        if (userIds.Count == 0) return false;
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.Managers.AsNoTracking().AnyAsync(m => userIds.Contains(m.UserId), ct);
    }

    public async Task AddProgramAsync(GrantProgram program, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        ctx.Programs.Add(program);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task UpdateProgramAsync(
        GrantProgram program, IReadOnlyList<GrantProgramManager> toAdd, IReadOnlyList<Guid> toRemove,
        CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        // Apply scalar fields and explicit manager deltas without attaching the detached grant graph.
        var tracked = await ctx.Programs.FirstAsync(p => p.Id == program.Id, ct);
        ctx.Entry(tracked).CurrentValues.SetValues(program);
        if (toRemove.Count > 0)
        {
            var rows = await ctx.Managers
                .Where(m => m.ProgramId == program.Id && toRemove.Contains(m.UserId))
                .ToListAsync(ct);
            ctx.Managers.RemoveRange(rows);
        }
        foreach (var manager in toAdd)
            ctx.Managers.Add(new GrantProgramManager
            {
                ProgramId = program.Id,
                UserId = manager.UserId,
                AddedByUserId = manager.AddedByUserId,
                AddedAt = manager.AddedAt
            });
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<int> AssignEventIdsAsync(
        IReadOnlyDictionary<Guid, Guid> eventByProgram, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var ids = eventByProgram.Keys.ToList();
        var programs = await ctx.Programs.Where(p => p.EventSettingsId == null && ids.Contains(p.Id)).ToListAsync(ct);
        foreach (var program in programs)
            program.EventSettingsId = eventByProgram[program.Id];
        await ctx.SaveChangesAsync(ct);
        return programs.Count;
    }

    public async Task AddGrantsAsync(IReadOnlyList<Grant> grants, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        foreach (var grant in grants)
            ctx.Grants.Add(Scalar(grant));
        await ctx.SaveChangesAsync(ct);
    }

    public async Task UpdateGrantAsync(Grant grant, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var tracked = await ctx.Grants.FirstAsync(g => g.Id == grant.Id, ct);
        ctx.Entry(tracked).CurrentValues.SetValues(Scalar(grant));
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<GrantUserRows> GetRowsForUsersAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var grants = await ctx.Grants.AsNoTracking().Include(g => g.Program)
            .Where(g => userIds.Contains(g.UserId)).ToListAsync(ct);
        var created = await ctx.Programs.AsNoTracking()
            .Where(p => userIds.Contains(p.CreatedByUserId)).ToListAsync(ct);
        var managerRows = await ctx.Managers.AsNoTracking().Include(m => m.Program)
            .Where(m => userIds.Contains(m.UserId)).ToListAsync(ct);
        var managersAdded = await ctx.Managers.AsNoTracking().Include(m => m.Program)
            .Where(m => userIds.Contains(m.AddedByUserId)).ToListAsync(ct);
        var issued = await ctx.Grants.AsNoTracking().Include(g => g.Program)
            .Where(g => userIds.Contains(g.IssuedByUserId)).ToListAsync(ct);
        var revoked = await ctx.Grants.AsNoTracking().Include(g => g.Program)
            .Where(g => g.RevokedByUserId != null && userIds.Contains(g.RevokedByUserId.Value))
            .ToListAsync(ct);
        return new GrantUserRows(grants, created, managerRows, managersAdded, issued, revoked);
    }

    public async Task EraseUserAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        ctx.Grants.RemoveRange(await ctx.Grants.Where(g => g.UserId == userId).ToListAsync(ct));
        ctx.Managers.RemoveRange(await ctx.Managers.Where(m => m.UserId == userId).ToListAsync(ct));
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<Guid>> ReassignToUserAsync(
        Guid sourceUserId, Guid targetUserId, Guid actorUserId, Instant now, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);

        // Two live grants in one program would break the one-live-grant index once re-keyed:
        // the newer is revoked and saved first, so the re-key below never collides.
        var sourceGrants = await ctx.Grants.Where(g => g.UserId == sourceUserId).ToListAsync(ct);
        var targetLive = await ctx.Grants.Where(g => g.UserId == targetUserId && g.RevokedAt == null).ToListAsync(ct);
        var revoked = new List<Guid>();
        foreach (var grant in sourceGrants.Where(g => g.RevokedAt is null))
        {
            var twin = targetLive.FirstOrDefault(t => t.ProgramId == grant.ProgramId);
            if (twin is null) continue;
            var drop = twin.IssuedAt <= grant.IssuedAt ? grant : twin;
            drop.RevokedAt = now;
            drop.RevokedByUserId = actorUserId;
            drop.RevokeReason = MergeRevokeReason;
            revoked.Add(drop.Id);
        }
        await ctx.SaveChangesAsync(ct);

        foreach (var grant in sourceGrants)
            grant.UserId = targetUserId;

        // Manager rows: a survivor already managing the same program keeps one row. The new row is
        // re-keyed here: the actor-column pass below queries the store and never sees it.
        var sourceManagers = await ctx.Managers.Where(m => m.UserId == sourceUserId).ToListAsync(ct);
        var targetPrograms = await ctx.Managers.Where(m => m.UserId == targetUserId)
            .Select(m => m.ProgramId).ToListAsync(ct);
        foreach (var row in sourceManagers)
        {
            ctx.Managers.Remove(row);
            if (!targetPrograms.Contains(row.ProgramId))
                ctx.Managers.Add(new GrantProgramManager
                {
                    ProgramId = row.ProgramId,
                    UserId = targetUserId,
                    AddedByUserId = row.AddedByUserId == sourceUserId ? targetUserId : row.AddedByUserId,
                    AddedAt = row.AddedAt
                });
        }

        foreach (var p in await ctx.Programs.Where(p => p.CreatedByUserId == sourceUserId).ToListAsync(ct))
            p.CreatedByUserId = targetUserId;
        foreach (var m in await ctx.Managers.Where(m => m.AddedByUserId == sourceUserId).ToListAsync(ct))
            m.AddedByUserId = targetUserId;
        foreach (var g in await ctx.Grants.Where(g => g.IssuedByUserId == sourceUserId).ToListAsync(ct))
            g.IssuedByUserId = targetUserId;
        foreach (var g in await ctx.Grants.Where(g => g.RevokedByUserId == sourceUserId).ToListAsync(ct))
            g.RevokedByUserId = targetUserId;

        await ctx.SaveChangesAsync(ct);
        return revoked;
    }

    private static IQueryable<GrantProgram> ProgramsWithChildren(GrantsDbContext ctx) =>
        // Identity resolution rather than plain AsNoTracking: the child → Program back-references
        // make the graph cyclic, which no-tracking queries refuse.
        ctx.Programs.AsNoTrackingWithIdentityResolution()
            .Include(p => p.Managers)
            .Include(p => p.Grants);

    /// <summary>A copy without navigations, so a save never drags a detached program graph along.</summary>
    private static Grant Scalar(Grant g) => new()
    {
        Id = g.Id,
        ProgramId = g.ProgramId,
        UserId = g.UserId,
        SourceRef = g.SourceRef,
        ExpiresOn = g.ExpiresOn,
        IssuedAt = g.IssuedAt,
        IssuedByUserId = g.IssuedByUserId,
        NotifiedAt = g.NotifiedAt,
        RevokedAt = g.RevokedAt,
        RevokedByUserId = g.RevokedByUserId,
        RevokeReason = g.RevokeReason
    };
}
