using Humans.Preapprovals.Contracts;
using Humans.Preapprovals.Domain;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Humans.Preapprovals.Data;

internal sealed class PreapprovalRepository(IDbContextFactory<PreapprovalsDbContext> factory) : IPreapprovalRepository
{
    // ── Reads ─────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<PreapprovalProgram>> GetProgramsAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ProgramsWithChildren(ctx).OrderBy(p => p.ClaimBy).ThenBy(p => p.Name).ToListAsync(ct);
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
            .OrderBy(g => g.Program.ClaimBy).ThenBy(g => g.CreatedAt)
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

    // ── Writes ────────────────────────────────────────────────────────────

    public async Task AddProgramAsync(PreapprovalProgram program, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        ctx.Programs.Add(program);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task UpdateProgramAsync(PreapprovalProgram program, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        // Scalars only: the detached graph may carry managers and grants that other calls own.
        var tracked = await ctx.Programs.FirstAsync(p => p.Id == program.Id, ct);
        ctx.Entry(tracked).CurrentValues.SetValues(program);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task SetManagersAsync(
        Guid programId, IReadOnlyList<ProgramManager> toAdd, IReadOnlyList<Guid> toRemove,
        CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        if (toRemove.Count > 0)
        {
            var rows = await ctx.Managers
                .Where(m => m.ProgramId == programId && toRemove.Contains(m.UserId))
                .ToListAsync(ct);
            ctx.Managers.RemoveRange(rows);
        }
        foreach (var manager in toAdd)
            ctx.Managers.Add(new ProgramManager
            {
                ProgramId = programId,
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

    public async Task<bool> AnyProgramAcceptsApplicationsAsync(LocalDate today, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.Programs.AsNoTracking()
            .AnyAsync(p => p.Status == ProgramStatus.Open && p.AcceptsApplications && p.ClaimBy >= today, ct);
    }

    // ── GDPR ──────────────────────────────────────────────────────────────

    public async Task<PreapprovalUserRows> GetRowsForUsersAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var grants = await ctx.Grants.AsNoTracking().Include(g => g.Program)
            .Where(g => userIds.Contains(g.BeneficiaryUserId)).OrderBy(g => g.CreatedAt).ToListAsync(ct);
        var created = await ctx.Programs.AsNoTracking()
            .Where(p => userIds.Contains(p.CreatedByUserId)).OrderBy(p => p.CreatedAt).ToListAsync(ct);
        var managersAdded = await ctx.Managers.AsNoTracking().Include(m => m.Program)
            .Where(m => userIds.Contains(m.AddedByUserId)).OrderBy(m => m.AddedAt).ToListAsync(ct);
        var managerRows = await ctx.Managers.AsNoTracking().Include(m => m.Program)
            .Where(m => userIds.Contains(m.UserId)).OrderBy(m => m.AddedAt).ToListAsync(ct);
        var decided = await ctx.Grants.AsNoTracking().Include(g => g.Program)
            .Where(g => g.DecidedByUserId != null && userIds.Contains(g.DecidedByUserId.Value))
            .OrderBy(g => g.DecidedAt).ToListAsync(ct);
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

        // Beneficiary: two Applied rows for one program collapse to the older one.
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

        // Manager rows: a survivor already managing the same program keeps one row.
        var sourceManagers = await ctx.Managers.Where(m => m.UserId == sourceUserId).ToListAsync(ct);
        var targetPrograms = await ctx.Managers.Where(m => m.UserId == targetUserId)
            .Select(m => m.ProgramId).ToListAsync(ct);
        foreach (var row in sourceManagers)
        {
            ctx.Managers.Remove(row);
            if (!targetPrograms.Contains(row.ProgramId))
                ctx.Managers.Add(new ProgramManager
                {
                    ProgramId = row.ProgramId, UserId = targetUserId,
                    AddedByUserId = row.AddedByUserId, AddedAt = row.AddedAt
                });
        }

        // Actor columns.
        foreach (var p in await ctx.Programs.Where(p => p.CreatedByUserId == sourceUserId).ToListAsync(ct))
            p.CreatedByUserId = targetUserId;
        foreach (var m in await ctx.Managers.Where(m => m.AddedByUserId == sourceUserId).ToListAsync(ct))
            m.AddedByUserId = targetUserId;
        foreach (var g in await ctx.Grants.Where(g => g.DecidedByUserId == sourceUserId).ToListAsync(ct))
            g.DecidedByUserId = targetUserId;

        await ctx.SaveChangesAsync(ct);
        return dropped;
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static IQueryable<PreapprovalProgram> ProgramsWithChildren(PreapprovalsDbContext ctx) =>
        // Identity resolution rather than plain AsNoTracking: the child → Program back-references
        // make the graph cyclic, which no-tracking queries refuse.
        ctx.Programs.AsNoTrackingWithIdentityResolution()
            .Include(p => p.Managers)
            .Include(p => p.Grants);

    /// <summary>A copy without navigations, so a save never drags a detached program graph along.</summary>
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
