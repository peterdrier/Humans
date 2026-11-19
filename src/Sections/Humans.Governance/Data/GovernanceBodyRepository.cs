using Humans.Governance.Domain;
using Microsoft.EntityFrameworkCore;

namespace Humans.Governance.Data;

internal sealed class GovernanceBodyRepository(IDbContextFactory<GovernanceDbContext> factory) : IGovernanceBodyRepository
{
    public async Task<IReadOnlyList<GovernanceBodyLinks>> GetAllAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.GovernanceBodyLinks.AsNoTracking().Include(b => b.Files).ToListAsync(ct);
    }

    public async Task ReplaceAsync(GovernanceBodyLinks links, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var row = await ctx.GovernanceBodyLinks.Include(b => b.Files).SingleOrDefaultAsync(b => b.Body == links.Body, ct);
        if (row is null)
        {
            row = new GovernanceBodyLinks { Body = links.Body };
            ctx.GovernanceBodyLinks.Add(row);
        }
        else
        {
            ctx.GovernanceBodyFiles.RemoveRange(row.Files);
            row.Files.Clear();
        }

        row.GoogleGroupPrefix = links.GoogleGroupPrefix;
        row.UpdatedAt = links.UpdatedAt;
        foreach (var file in links.Files)
        {
            row.Files.Add(new GovernanceBodyFile
            {
                Id = file.Id,
                Body = links.Body,
                GoogleId = file.GoogleId,
                ResourceType = file.ResourceType,
                Name = file.Name,
                Url = file.Url,
                PermissionLevel = file.PermissionLevel,
            });
        }

        await ctx.SaveChangesAsync(ct);
    }
}
