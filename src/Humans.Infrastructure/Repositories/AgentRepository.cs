using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Humans.Infrastructure.Repositories;

public sealed class AgentRepository : IAgentRepository
{
    private readonly HumansDbContext _db;
    private readonly IClock _clock;

    public AgentRepository(HumansDbContext db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }

    // ---- Settings ----------------------------------------------------------

    public Task<AgentSettings?> GetSettingsAsync(CancellationToken cancellationToken) =>
        _db.AgentSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, cancellationToken);

    public async Task<AgentSettings> UpdateSettingsAsync(
        Action<AgentSettings> mutator, Instant updatedAt, CancellationToken cancellationToken)
    {
        var row = await _db.AgentSettings.FirstAsync(s => s.Id == 1, cancellationToken);
        mutator(row);
        row.UpdatedAt = updatedAt;
        await _db.SaveChangesAsync(cancellationToken);
        return row;
    }

    // ---- Conversations + messages -----------------------------------------

    public Task<AgentConversation?> GetConversationByIdAsync(Guid id, CancellationToken cancellationToken) =>
        _db.AgentConversations
            .Include(c => c.Messages.OrderBy(m => m.CreatedAt))
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

    public async Task<AgentConversation> CreateConversationAsync(Guid userId, string locale, CancellationToken cancellationToken)
    {
        var now = _clock.GetCurrentInstant();
        var conv = new AgentConversation
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Locale = locale,
            StartedAt = now,
            LastMessageAt = now,
            MessageCount = 0
        };
        _db.AgentConversations.Add(conv);
        await _db.SaveChangesAsync(cancellationToken);
        return conv;
    }

    public async Task AppendMessageAsync(AgentMessage message, CancellationToken cancellationToken)
    {
        _db.AgentMessages.Add(message);

        var conv = await _db.AgentConversations.FirstAsync(c => c.Id == message.ConversationId, cancellationToken);
        conv.MessageCount += 1;
        conv.LastMessageAt = message.CreatedAt;

        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AgentConversation>> ListConversationsForUserAsync(Guid userId, int take, CancellationToken cancellationToken) =>
        await _db.AgentConversations
            .AsNoTracking()
            .Where(c => c.UserId == userId)
            .OrderByDescending(c => c.LastMessageAt)
            .Take(take)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<AgentConversation>> ListConversationsForUserWithMessagesAsync(Guid userId, CancellationToken cancellationToken) =>
        await _db.AgentConversations
            .AsNoTracking()
            .Where(c => c.UserId == userId)
            .Include(c => c.Messages.OrderBy(m => m.CreatedAt))
            .OrderByDescending(c => c.LastMessageAt)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<AgentConversation>> ListAllConversationsAsync(
        bool refusalsOnly, bool handoffsOnly, Guid? userId, int take, int skip,
        CancellationToken cancellationToken)
    {
        IQueryable<AgentConversation> q = _db.AgentConversations.AsNoTracking();

        if (userId is Guid u) q = q.Where(c => c.UserId == u);
        if (refusalsOnly) q = q.Where(c => c.Messages.Any(m => m.RefusalReason != null));
        if (handoffsOnly) q = q.Where(c => c.Messages.Any(m => m.HandedOffToFeedbackId != null));

        return await q.OrderByDescending(c => c.LastMessageAt)
            .Skip(skip).Take(take)
            .ToListAsync(cancellationToken);
    }

    public async Task DeleteConversationAsync(Guid id, CancellationToken cancellationToken)
    {
        var conv = await _db.AgentConversations.FindAsync([id], cancellationToken);
        if (conv is null) return;
        _db.AgentConversations.Remove(conv);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<int> PurgeConversationsOlderThanAsync(Instant cutoff, CancellationToken cancellationToken)
    {
        // Standard load+remove pattern (vs ExecuteDeleteAsync) — small scale,
        // retention runs once a day, and the in-memory provider used in unit
        // tests doesn't support ExecuteDeleteAsync.
        var stale = await _db.AgentConversations
            .Where(c => c.LastMessageAt < cutoff)
            .ToListAsync(cancellationToken);
        if (stale.Count == 0) return 0;
        _db.AgentConversations.RemoveRange(stale);
        await _db.SaveChangesAsync(cancellationToken);
        return stale.Count;
    }
}
