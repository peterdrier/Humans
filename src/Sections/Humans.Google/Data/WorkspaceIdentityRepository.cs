using Humans.Base.Data;
using Humans.Google.Contracts;
using Humans.Google.Domain;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Humans.Google.Data;

internal sealed class WorkspaceIdentityRepository(IDbContextFactory<GoogleIntegrationDbContext> factory)
    : SectionRepository<GoogleIntegrationDbContext>(factory), IWorkspaceIdentityRepository
{
    public async Task<WorkspaceIdentityRecord?> GetAsync(Guid userId, CancellationToken ct = default) =>
        await RunAsync(ctx => ctx.WorkspaceIdentities.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == userId, ct), ct);

    public async Task<IReadOnlyList<WorkspaceIdentityRecord>> GetAllAsync(CancellationToken ct = default) =>
        await RunAsync(ctx => ctx.WorkspaceIdentities.AsNoTracking().ToListAsync(ct), ct);

    public async Task UpsertAsync(
        Guid userId, string? email, GoogleEmailStatus status, Instant now, CancellationToken ct = default)
    {
        await using var ctx = await CreateDbContextAsync(ct);
        var row = await ctx.WorkspaceIdentities.SingleOrDefaultAsync(x => x.UserId == userId, ct);
        if (row is null)
        {
            ctx.WorkspaceIdentities.Add(new WorkspaceIdentityRecord
            {
                UserId = userId,
                Email = email,
                Status = status,
                UpdatedAt = now,
            });
        }
        else
        {
            if (!string.Equals(row.Email, email, StringComparison.OrdinalIgnoreCase))
                row.DriveEmail = null;
            row.Email = email;
            row.Status = status;
            row.UpdatedAt = now;
        }
        await ctx.SaveChangesAsync(ct);
    }

    public async Task SetDriveEmailAsync(
        Guid userId, string email, GoogleEmailStatus status, string driveEmail, Instant now, CancellationToken ct = default)
    {
        await using var ctx = await CreateDbContextAsync(ct);
        var row = await ctx.WorkspaceIdentities.SingleOrDefaultAsync(x => x.UserId == userId, ct);
        if (row is null)
        {
            row = new WorkspaceIdentityRecord { UserId = userId };
            ctx.WorkspaceIdentities.Add(row);
        }
        row.Email = email;
        row.Status = status;
        row.DriveEmail = driveEmail;
        row.UpdatedAt = now;
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<bool> DeleteAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await CreateDbContextAsync(ct);
        var row = await ctx.WorkspaceIdentities.SingleOrDefaultAsync(x => x.UserId == userId, ct);
        if (row is null) return false;
        ctx.WorkspaceIdentities.Remove(row);
        await ctx.SaveChangesAsync(ct);
        return true;
    }
}
