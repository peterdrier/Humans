using Humans.Cantina.Domain;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Humans.Cantina.Data;

internal sealed class DietaryProfileRepository(IDbContextFactory<CantinaDbContext> factory)
    : IDietaryProfileRepository
{
    public async Task<DietaryProfile?> GetAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.DietaryProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == userId, ct);
    }

    public async Task<IReadOnlyList<DietaryProfile>> GetManyAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default)
    {
        if (userIds.Count == 0) return [];
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.DietaryProfiles.AsNoTracking().Where(x => userIds.Contains(x.UserId)).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<DietaryProfile>> GetAllAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.DietaryProfiles.AsNoTracking().ToListAsync(ct);
    }

    public async Task UpsertAsync(DietaryProfile values, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var row = await ctx.DietaryProfiles.SingleOrDefaultAsync(x => x.UserId == values.UserId, ct);
        if (row is null)
        {
            ctx.DietaryProfiles.Add(Copy(values, values.UserId));
        }
        else
        {
            row.DietaryPreference = values.DietaryPreference;
            row.Allergies = [.. values.Allergies];
            row.AllergyOtherText = values.AllergyOtherText;
            row.Intolerances = [.. values.Intolerances];
            row.IntoleranceOtherText = values.IntoleranceOtherText;
            row.UpdatedAt = values.UpdatedAt;
        }
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<bool> TryInsertAsync(DietaryProfile values, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        if (await ctx.DietaryProfiles.AnyAsync(x => x.UserId == values.UserId, ct)) return false;
        ctx.DietaryProfiles.Add(Copy(values, values.UserId));
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> DeleteAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var row = await ctx.DietaryProfiles.SingleOrDefaultAsync(x => x.UserId == userId, ct);
        if (row is null) return false;
        ctx.DietaryProfiles.Remove(row);
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task ReassignAsync(Guid fromUserId, Guid toUserId, Instant now, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var source = await ctx.DietaryProfiles.SingleOrDefaultAsync(x => x.UserId == fromUserId, ct);
        if (source is null) return;

        // The key is the user id, so a move is a delete plus an insert in one save.
        ctx.DietaryProfiles.Remove(source);
        if (!await ctx.DietaryProfiles.AnyAsync(x => x.UserId == toUserId, ct))
        {
            var moved = Copy(source, toUserId);
            moved.UpdatedAt = now;
            ctx.DietaryProfiles.Add(moved);
        }
        await ctx.SaveChangesAsync(ct);
    }

    private static DietaryProfile Copy(DietaryProfile v, Guid userId) => new()
    {
        UserId = userId,
        DietaryPreference = v.DietaryPreference,
        Allergies = [.. v.Allergies],
        AllergyOtherText = v.AllergyOtherText,
        Intolerances = [.. v.Intolerances],
        IntoleranceOtherText = v.IntoleranceOtherText,
        UpdatedAt = v.UpdatedAt,
    };
}
