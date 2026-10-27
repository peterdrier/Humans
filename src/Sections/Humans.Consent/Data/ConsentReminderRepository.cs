using Humans.Consent.Domain;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Humans.Consent.Data;

internal sealed class ConsentReminderRepository(IDbContextFactory<LegalDbContext> factory)
    : IConsentReminderRepository
{
    public async Task<IReadOnlyList<ConsentReminder>> GetByUserIdsAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default)
    {
        if (userIds.Count == 0)
            return [];

        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.ConsentReminders.AsNoTracking().Where(r => userIds.Contains(r.UserId)).ToListAsync(ct);
    }

    public async Task UpsertAsync(Guid userId, Instant sentAt, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var row = await ctx.ConsentReminders.SingleOrDefaultAsync(r => r.UserId == userId, ct);
        if (row is null)
            ctx.ConsentReminders.Add(new ConsentReminder { UserId = userId, LastSentAt = sentAt });
        else
            row.LastSentAt = sentAt;
        await ctx.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var row = await ctx.ConsentReminders.SingleOrDefaultAsync(r => r.UserId == userId, ct);
        if (row is null)
            return;
        ctx.ConsentReminders.Remove(row);
        await ctx.SaveChangesAsync(ct);
    }
}
