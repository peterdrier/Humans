using Humans.Governance.Domain;
using Microsoft.EntityFrameworkCore;
using NodaTime;

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

    public async Task<IReadOnlyList<GovernanceBoardPosition>> GetBoardPositionsAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.GovernanceBoardPositions.AsNoTracking().ToListAsync(ct);
    }

    public async Task SetBoardPositionAsync(BoardPosition position, Guid? userId, Instant now, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var rows = await ctx.GovernanceBoardPositions.ToListAsync(ct);
        ctx.GovernanceBoardPositions.RemoveRange(rows.Where(r => r.Position == position || r.UserId == userId));
        await ctx.SaveChangesAsync(ct);
        if (userId is { } holder)
        {
            ctx.GovernanceBoardPositions.Add(new GovernanceBoardPosition { Position = position, UserId = holder, UpdatedAt = now });
            await ctx.SaveChangesAsync(ct);
        }
    }

    public async Task ClearBoardPositionForUserAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        ctx.GovernanceBoardPositions.RemoveRange(await ctx.GovernanceBoardPositions.Where(r => r.UserId == userId).ToListAsync(ct));
        await ctx.SaveChangesAsync(ct);
    }

    public async Task ReassignBoardPositionAsync(Guid fromUserId, Guid toUserId, Instant now, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var rows = await ctx.GovernanceBoardPositions.Where(r => r.UserId == fromUserId || r.UserId == toUserId).ToListAsync(ct);
        if (rows.Find(r => r.UserId == fromUserId) is not { } from)
            return;
        ctx.GovernanceBoardPositions.Remove(from);
        await ctx.SaveChangesAsync(ct);
        if (!rows.Exists(r => r.UserId == toUserId))
        {
            ctx.GovernanceBoardPositions.Add(new GovernanceBoardPosition { Position = from.Position, UserId = toUserId, UpdatedAt = now });
            await ctx.SaveChangesAsync(ct);
        }
    }
}
