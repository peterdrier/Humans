using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Humans.Users.Data.Repositories;

internal sealed partial class UserRepository
{
    public async Task<IReadOnlyList<ContactField>> GetContactFieldsByUserIdReadOnlyAsync(
        Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.ContactFields
            .AsNoTracking()
            .Where(cf => cf.UserId == userId)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<ContactField>> GetAllContactFieldsAsync(CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.ContactFields
            .AsNoTracking()
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<ContactField>> GetContactFieldsByUserIdForMutationAsync(
        Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.ContactFields
            .AsNoTracking()
            .Where(cf => cf.UserId == userId)
            .ToListAsync(ct);
    }

    public async Task BatchSaveAsync(
        IReadOnlyList<ContactField> toAdd,
        IReadOnlyList<ContactField> toUpdate,
        IReadOnlyList<ContactField> toRemove,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        if (toRemove.Count > 0)
            ctx.ContactFields.RemoveRange(toRemove);
        if (toUpdate.Count > 0)
            ctx.ContactFields.UpdateRange(toUpdate);
        if (toAdd.Count > 0)
            ctx.ContactFields.AddRange(toAdd);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task ReassignToUserAsync(
        Guid sourceUserId, Guid targetUserId, Instant updatedAt,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        if (!await ctx.Profiles.AnyAsync(p => p.UserId == targetUserId, ct))
            return;

        var sourceRows = await ctx.ContactFields
            .Where(cf => cf.UserId == sourceUserId)
            .ToListAsync(ct);
        var targetRows = await ctx.ContactFields
            .Where(cf => cf.UserId == targetUserId)
            .ToListAsync(ct);

        // Value has no canonical comparison, so match case-insensitively; target wins.
        var targetKeys = targetRows
            .Select(cf => (cf.FieldType, Value: cf.Value.ToLowerInvariant()))
            .ToHashSet();

        foreach (var src in sourceRows)
        {
            var key = (src.FieldType, Value: src.Value.ToLowerInvariant());
            if (targetKeys.Contains(key))
            {
                ctx.ContactFields.Remove(src);
            }
            else
            {
                src.UserId = targetUserId;
                src.UpdatedAt = updatedAt;
                targetKeys.Add(key);
            }
        }

        await ctx.SaveChangesAsync(ct);
    }
}
