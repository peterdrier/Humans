using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Humans.Infrastructure.Repositories;

public sealed class AgentConversationRepository : IAgentConversationRepository
{
    private readonly HumansDbContext _db;
    private readonly IClock _clock;

    public AgentConversationRepository(HumansDbContext db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }

    public Task<AgentConversation?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        _db.AgentConversations
            .Include(c => c.Messages.OrderBy(m => m.CreatedAt))
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

    public async Task<AgentConversation> CreateAsync(Guid userId, string locale, CancellationToken cancellationToken)
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

    public async Task<IReadOnlyList<AgentConversation>> ListForUserAsync(Guid userId, int take, CancellationToken cancellationToken) =>
        await _db.AgentConversations
            .AsNoTracking()
            .Where(c => c.UserId == userId)
            .OrderByDescending(c => c.LastMessageAt)
            .Take(take)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<AgentConversation>> ListAllAsync(
        bool refusalsOnly, bool handoffsOnly, Guid? userId, int take, int skip,
        CancellationToken cancellationToken)
    {
        IQueryable<AgentConversation> q = _db.AgentConversations
            .AsNoTracking()
            .Include(c => c.User);

        if (userId is Guid u) q = q.Where(c => c.UserId == u);
        if (refusalsOnly) q = q.Where(c => c.Messages.Any(m => m.RefusalReason != null));
        if (handoffsOnly) q = q.Where(c => c.Messages.Any(m => m.HandedOffToFeedbackId != null));

        return await q.OrderByDescending(c => c.LastMessageAt)
            .Skip(skip).Take(take)
            .ToListAsync(cancellationToken);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var conv = await _db.AgentConversations.FindAsync([id], cancellationToken);
        if (conv is null) return;
        _db.AgentConversations.Remove(conv);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<int> PurgeOlderThanAsync(Instant cutoff, CancellationToken cancellationToken)
    {
        return await _db.AgentConversations
            .Where(c => c.LastMessageAt < cutoff)
            .ExecuteDeleteAsync(cancellationToken);
    }
}
