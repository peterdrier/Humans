using Humans.Domain.Entities;
using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Humans.Infrastructure.Services;

internal static class TeamResourcePersistence
{
    public static async Task<IReadOnlyList<GoogleResource>> GetActiveTeamResourcesAsync(
        HumansDbContext dbContext,
        Guid teamId,
        CancellationToken ct = default)
    {
        return await dbContext.GoogleResources
            .Where(r => r.TeamId == teamId && r.IsActive)
            .OrderBy(r => r.ProvisionedAt)
            .AsNoTracking()
            .ToListAsync(ct);
    }

    public static async Task<GoogleResource?> DeactivateResourceAsync(
        HumansDbContext dbContext,
        Guid resourceId,
        CancellationToken ct = default)
    {
        var resource = await dbContext.GoogleResources
            .FirstOrDefaultAsync(r => r.Id == resourceId, ct);

        if (resource == null)
        {
            return null;
        }

        resource.IsActive = false;
        await dbContext.SaveChangesAsync(ct);
        return resource;
    }

    public static async Task<GoogleResource?> GetResourceByIdAsync(
        HumansDbContext dbContext,
        Guid resourceId,
        CancellationToken ct = default)
    {
        return await dbContext.GoogleResources
            .AsNoTracking()
            .Include(r => r.Team)
            .FirstOrDefaultAsync(r => r.Id == resourceId, ct);
    }
}
