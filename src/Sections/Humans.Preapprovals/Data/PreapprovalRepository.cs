using Humans.Preapprovals.Contracts;
using Humans.Preapprovals.Domain;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Humans.Preapprovals.Data;

internal sealed class PreapprovalRepository(IDbContextFactory<PreapprovalsDbContext> factory) : IPreapprovalRepository
{
    public async Task<IReadOnlyList<PreapprovalProgram>> GetProgramsAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ProgramsWithChildren(ctx).ToListAsync(ct);
    }

    public async Task<PreapprovalProgram?> GetProgramAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ProgramsWithChildren(ctx).FirstOrDefaultAsync(p => p.Id == id, ct);
    }

    public async Task<PreapprovalGrant?> GetGrantAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.Grants.AsNoTrackingWithIdentityResolution()
            .Include(g => g.Program).ThenInclude(p => p.Managers)
            .FirstOrDefaultAsync(g => g.Id == id, ct);
    }

    public async Task<IReadOnlyList<PreapprovalGrant>> GetGrantsForUsersAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default)
    {
        if (userIds.Count == 0) return [];
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.Grants.AsNoTracking()
            .Include(g => g.Program)
            .Where(g => userIds.Contains(g.BeneficiaryUserId))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<PreapprovalGrant>> GetGrantsByStatusAsync(GrantStatus status, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.Grants.AsNoTracking()
            .Include(g => g.Program)
            .Where(g => g.Status == status)
            .ToListAsync(ct);
    }

    public async Task AddProgramAsync(PreapprovalProgram program, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        ctx.Programs.Add(program);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task UpdateProgramAsync(
        PreapprovalProgram program, IReadOnlyList<ProgramManager> toAdd, IReadOnlyList<Guid> toRemove,
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
            ctx.Managers.Add(new ProgramManager
            {
                ProgramId = program.Id,
                UserId = manager.UserId,
                AddedByUserId = manager.AddedByUserId,
                AddedAt = manager.AddedAt
            });
        await ctx.SaveChangesAsync(ct);
    }

    public async Task AddGrantsAsync(IReadOnlyList<PreapprovalGrant> grants, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        foreach (var grant in grants)
            ctx.Grants.Add(Scalar(grant));
        await ctx.SaveChangesAsync(ct);
    }

    public async Task UpdateGrantAsync(PreapprovalGrant grant, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var tracked = await ctx.Grants.FirstAsync(g => g.Id == grant.Id, ct);
        ctx.Entry(tracked).CurrentValues.SetValues(Scalar(grant));
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<ReminderSent>> GetRemindersSentAsync(
        IReadOnlyCollection<Guid> grantIds, CancellationToken ct = default)
    {
        if (grantIds.Count == 0) return [];
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.RemindersSent.AsNoTracking()
            .Where(r => grantIds.Contains(r.GrantId))
            .ToListAsync(ct);
    }

    public async Task AddReminderSentAsync(ReminderSent reminder, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        ctx.RemindersSent.Add(reminder);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<bool> HasGrantsForUsersAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct = default)
    {
        if (userIds.Count == 0) return false;
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.Grants.AsNoTracking().AnyAsync(g => userIds.Contains(g.BeneficiaryUserId), ct);
    }

    public async Task<bool> ManagesAnyProgramAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct = default)
    {
        if (userIds.Count == 0) return false;
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.Managers.AsNoTracking().AnyAsync(m => userIds.Contains(m.UserId), ct);
    }

    public async Task<IReadOnlyList<Guid>> GetGrantIdsOfManagedProgramsAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default)
    {
        if (userIds.Count == 0) return [];
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.Grants.AsNoTracking()
            .Where(g => g.Program.Managers.Any(m => userIds.Contains(m.UserId)))
            .Select(g => g.Id)
            .ToListAsync(ct);
    }

    public async Task<bool> AnyProgramAcceptsApplicationsAsync(LocalDate today, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.Programs.AsNoTracking()
            .AnyAsync(p => p.Status == ProgramStatus.Open && p.AcceptsApplications && p.ClaimBy >= today, ct);
    }

    public async Task<PreapprovalUserRows> GetRowsForUsersAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var grants = await ctx.Grants.AsNoTracking().Include(g => g.Program)
            .Where(g => userIds.Contains(g.BeneficiaryUserId)).ToListAsync(ct);
        var created = await ctx.Programs.AsNoTracking()
            .Where(p => userIds.Contains(p.CreatedByUserId)).ToListAsync(ct);
        var managersAdded = await ctx.Managers.AsNoTracking().Include(m => m.Program)
            .Where(m => userIds.Contains(m.AddedByUserId)).ToListAsync(ct);
        var managerRows = await ctx.Managers.AsNoTracking().Include(m => m.Program)
            .Where(m => userIds.Contains(m.UserId)).ToListAsync(ct);
        var decided = await ctx.Grants.AsNoTracking().Include(g => g.Program)
            .Where(g => g.DecidedByUserId != null && userIds.Contains(g.DecidedByUserId.Value))
            .ToListAsync(ct);
        return new PreapprovalUserRows(grants, created, managersAdded, managerRows, decided);
    }

    public async Task EraseUserAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var applications = await ctx.Grants
            .Where(g => g.BeneficiaryUserId == userId && g.Status == GrantStatus.Applied)
            .ToListAsync(ct);
        ctx.Grants.RemoveRange(applications);
        var managerRows = await ctx.Managers.Where(m => m.UserId == userId).ToListAsync(ct);
        ctx.Managers.RemoveRange(managerRows);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<Guid>> ReassignToUserAsync(
        Guid sourceUserId, Guid targetUserId, Instant now, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var dropped = new List<Guid>();

        // Two Applied rows for one program collapse to the older one.
        var sourceGrants = await ctx.Grants.Where(g => g.BeneficiaryUserId == sourceUserId).ToListAsync(ct);
        var targetApplications = await ctx.Grants
            .Where(g => g.BeneficiaryUserId == targetUserId && g.Status == GrantStatus.Applied)
            .ToListAsync(ct);
        foreach (var grant in sourceGrants)
        {
            var twin = grant.Status == GrantStatus.Applied
                ? targetApplications.FirstOrDefault(t => t.ProgramId == grant.ProgramId)
                : null;
            if (twin is null)
            {
                grant.BeneficiaryUserId = targetUserId;
                grant.UpdatedAt = now;
                continue;
            }
            var (keep, drop) = twin.CreatedAt <= grant.CreatedAt ? (twin, grant) : (grant, twin);
            keep.BeneficiaryUserId = targetUserId;
            keep.UpdatedAt = now;
            ctx.Grants.Remove(drop);
            dropped.Add(drop.Id);
        }

        // A survivor already managing the same program keeps one row.
        var sourceManagers = await ctx.Managers.Where(m => m.UserId == sourceUserId).ToListAsync(ct);
        var targetPrograms = await ctx.Managers.Where(m => m.UserId == targetUserId)
            .Select(m => m.ProgramId).ToListAsync(ct);
        foreach (var row in sourceManagers)
        {
            ctx.Managers.Remove(row);
            if (!targetPrograms.Contains(row.ProgramId))
                ctx.Managers.Add(new ProgramManager
                {
                    ProgramId = row.ProgramId,
                    UserId = targetUserId,
                    AddedByUserId = row.AddedByUserId,
                    AddedAt = row.AddedAt
                });
        }

        foreach (var p in await ctx.Programs.Where(p => p.CreatedByUserId == sourceUserId).ToListAsync(ct))
            p.CreatedByUserId = targetUserId;
        foreach (var m in await ctx.Managers.Where(m => m.AddedByUserId == sourceUserId).ToListAsync(ct))
            m.AddedByUserId = targetUserId;
        foreach (var g in await ctx.Grants.Where(g => g.DecidedByUserId == sourceUserId).ToListAsync(ct))
            g.DecidedByUserId = targetUserId;

        await ctx.SaveChangesAsync(ct);
        return dropped;
    }

    private static IQueryable<PreapprovalProgram> ProgramsWithChildren(PreapprovalsDbContext ctx) =>
        // The child → Program back-references make the graph cyclic, which plain no-tracking queries refuse.
        ctx.Programs.AsNoTrackingWithIdentityResolution()
            .Include(p => p.Managers)
            .Include(p => p.Grants);

    /// <summary>Without navigations, so a save never drags a detached program graph along.</summary>
    private static PreapprovalGrant Scalar(PreapprovalGrant g) => new()
    {
        Id = g.Id,
        ProgramId = g.ProgramId,
        BeneficiaryUserId = g.BeneficiaryUserId,
        Label = g.Label,
        MaxAmount = g.MaxAmount,
        Status = g.Status,
        ApplicationNote = g.ApplicationNote,
        RequestedAmount = g.RequestedAmount,
        DecidedByUserId = g.DecidedByUserId,
        DecidedAt = g.DecidedAt,
        ExpenseReportId = g.ExpenseReportId,
        CreatedAt = g.CreatedAt,
        UpdatedAt = g.UpdatedAt
    };
}
