using Microsoft.EntityFrameworkCore;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Infrastructure.Repositories.Users;

/// <summary>
/// Contact-field operations on <see cref="UserRepository"/>.
/// </summary>
internal sealed partial class UserRepository
{
    public async Task<IReadOnlyList<ContactField>> GetByProfileIdReadOnlyAsync(
        Guid profileId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.ContactFields
            .AsNoTracking()
            .Where(cf => cf.ProfileId == profileId)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<ContactField>> GetAllContactFieldsAsync(CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.ContactFields
            .AsNoTracking()
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<ContactField>> GetByProfileIdForMutationAsync(
        Guid profileId, CancellationToken ct = default)
    {
        // With IDbContextFactory the context is short-lived, so returned entities
        // are detached. Callers must pass mutated entities explicitly to BatchSaveAsync.
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.ContactFields
            .AsNoTracking()
            .Where(cf => cf.ProfileId == profileId)
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

    public async Task<int> ReassignToUserAsync(
        Guid sourceUserId, Guid targetUserId, Instant updatedAt,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        // Resolve User -> Profile for both sides. ContactField FKs to Profile,
        // not User, so the bulk-move pivots on profile id.
        var sourceProfileId = await ctx.Profiles
            .Where(p => p.UserId == sourceUserId)
            .Select(p => (Guid?)p.Id)
            .FirstOrDefaultAsync(ct);
        if (sourceProfileId is null)
            return 0;

        var targetProfileId = await ctx.Profiles
            .Where(p => p.UserId == targetUserId)
            .Select(p => (Guid?)p.Id)
            .FirstOrDefaultAsync(ct);
        if (targetProfileId is null)
            return 0;

        var sourceRows = await ctx.ContactFields
            .Where(cf => cf.ProfileId == sourceProfileId.Value)
            .ToListAsync(ct);
        var targetRows = await ctx.ContactFields
            .Where(cf => cf.ProfileId == targetProfileId.Value)
            .ToListAsync(ct);

        // Dedup on (FieldType, Value). ContactField.Value has no canonical
        // comparison configured, so we use case-insensitive match to keep the
        // collapse correct for emails/usernames where casing may differ. Target
        // wins on collision.
        var targetKeys = targetRows
            .Select(cf => (cf.FieldType, Value: cf.Value.ToLowerInvariant()))
            .ToHashSet();

        foreach (var src in sourceRows)
        {
            var key = (src.FieldType, Value: src.Value.ToLowerInvariant());
            if (targetKeys.Contains(key))
            {
                // Target already has the same (FieldType, Value) — drop source.
                ctx.ContactFields.Remove(src);
            }
            else
            {
                // Re-FK to target's profile.
                ctx.Entry(src).Property(nameof(ContactField.ProfileId)).CurrentValue = targetProfileId.Value;
                src.UpdatedAt = updatedAt;
                targetKeys.Add(key);
            }
        }

        await ctx.SaveChangesAsync(ct);

        return await ctx.ContactFields
            .CountAsync(cf => cf.ProfileId == targetProfileId.Value, ct);
    }
}
