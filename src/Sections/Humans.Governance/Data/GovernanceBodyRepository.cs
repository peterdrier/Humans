using Humans.Base.Data;
using Humans.Governance.Domain;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Humans.Governance.Data;

internal sealed class GovernanceBodyRepository(IDbContextFactory<GovernanceDbContext> factory) : SectionRepository<GovernanceDbContext>(factory), IGovernanceBodyRepository
{
    public async Task<IReadOnlyList<GovernanceBodyLinks>> GetAllAsync(CancellationToken ct = default) =>
        await RunAsync(ctx => ctx.GovernanceBodyLinks.AsNoTracking().Include(b => b.Files).ToListAsync(ct), ct);

    public async Task ReplaceAsync(GovernanceBodyLinks links, CancellationToken ct = default)
    {
        await using var ctx = await CreateDbContextAsync(ct);
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

    public async Task<IReadOnlyList<GovernanceBoardPosition>> GetBoardPositionsAsync(CancellationToken ct = default) =>
        await RunAsync(ctx => ctx.GovernanceBoardPositions.AsNoTracking().ToListAsync(ct), ct);

    public async Task SetBoardPositionAsync(BoardPosition position, Guid? userId, Instant now, CancellationToken ct = default)
    {
        await using var ctx = await CreateDbContextAsync(ct);
        var rows = await ctx.GovernanceBoardPositions.ToListAsync(ct);
        var slot = rows.Find(r => r.Position == position);
        ctx.GovernanceBoardPositions.RemoveRange(rows.Where(r => r.UserId == userId && r.Position != position));
        if (userId is not { } holder)
        {
            if (slot is not null)
                ctx.GovernanceBoardPositions.Remove(slot);
        }
        else if (slot is null)
        {
            ctx.GovernanceBoardPositions.Add(new GovernanceBoardPosition { Position = position, UserId = holder, UpdatedAt = now });
        }
        else
        {
            slot.UserId = holder;
            slot.UpdatedAt = now;
        }

        await ctx.SaveChangesAsync(ct);
    }

    public async Task ClearBoardPositionForUserAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await CreateDbContextAsync(ct);
        ctx.GovernanceBoardPositions.RemoveRange(await ctx.GovernanceBoardPositions.Where(r => r.UserId == userId).ToListAsync(ct));
        await ctx.SaveChangesAsync(ct);
    }

    public async Task ReassignBoardPositionAsync(Guid fromUserId, Guid toUserId, Instant now, CancellationToken ct = default)
    {
        await using var ctx = await CreateDbContextAsync(ct);
        var rows = await ctx.GovernanceBoardPositions.Where(r => r.UserId == fromUserId || r.UserId == toUserId).ToListAsync(ct);
        if (rows.Find(r => r.UserId == fromUserId) is not { } from)
            return;
        if (rows.Exists(r => r.UserId == toUserId))
        {
            ctx.GovernanceBoardPositions.Remove(from);
        }
        else
        {
            from.UserId = toUserId;
            from.UpdatedAt = now;
        }

        await ctx.SaveChangesAsync(ct);
    }
}
