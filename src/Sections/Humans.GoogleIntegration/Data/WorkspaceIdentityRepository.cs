using Humans.GoogleIntegration.Contracts;
using Humans.GoogleIntegration.Domain;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Humans.GoogleIntegration.Data;

/// <summary>EF-backed <see cref="IWorkspaceIdentityRepository"/>. Singleton over
/// <see cref="IDbContextFactory{TContext}"/> per design-rules §15b.</summary>
internal sealed class WorkspaceIdentityRepository(IDbContextFactory<GoogleIntegrationDbContext> factory)
    : IWorkspaceIdentityRepository
{
    public async Task<WorkspaceIdentityRecord?> GetAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.WorkspaceIdentities.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == userId, ct);
    }

    public async Task<IReadOnlyList<WorkspaceIdentityRecord>> GetAllAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.WorkspaceIdentities.AsNoTracking().ToListAsync(ct);
    }

    public async Task UpsertAsync(
        Guid userId, string? email, GoogleEmailStatus status, Instant now, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
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
            row.Email = email;
            row.Status = status;
            row.UpdatedAt = now;
        }
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<bool> TryInsertAsync(
        Guid userId, string? email, GoogleEmailStatus status, Instant now, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        if (await ctx.WorkspaceIdentities.AnyAsync(x => x.UserId == userId, ct)) return false;
        ctx.WorkspaceIdentities.Add(new WorkspaceIdentityRecord
        {
            UserId = userId,
            Email = email,
            Status = status,
            UpdatedAt = now,
        });
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> DeleteAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var row = await ctx.WorkspaceIdentities.SingleOrDefaultAsync(x => x.UserId == userId, ct);
        if (row is null) return false;
        ctx.WorkspaceIdentities.Remove(row);
        await ctx.SaveChangesAsync(ct);
        return true;
    }
}
