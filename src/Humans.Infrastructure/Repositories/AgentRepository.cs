using Humans.Application.Interfaces.Repositories;
using Humans.Application.Models;
using Humans.Domain.Entities;
using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Humans.Infrastructure.Repositories;

internal sealed class AgentRepository(HumansDbContext db, IClock clock) : IAgentRepository
{
    // ---- Settings ----------------------------------------------------------

    public Task<AgentSettings?> GetSettingsAsync(CancellationToken cancellationToken) =>
        db.AgentSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, cancellationToken);

    public async Task<AgentSettings> UpdateSettingsAsync(
        Action<AgentSettings> mutator, Instant updatedAt, CancellationToken cancellationToken)
    {
        var row = await db.AgentSettings.FirstAsync(s => s.Id == 1, cancellationToken);
        mutator(row);
        row.UpdatedAt = updatedAt;
        await db.SaveChangesAsync(cancellationToken);
        return row;
    }

    // ---- Conversations + messages -----------------------------------------

    public Task<AgentConversation?> GetConversationByIdAsync(Guid id, CancellationToken cancellationToken) =>
        db.AgentConversations
            // arch:db-sort-ok identity-ordered chronological message stream
            .Include(c => c.Messages.OrderBy(m => m.CreatedAt))
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

    public async Task<AgentConversation> CreateConversationAsync(Guid userId, string locale, CancellationToken cancellationToken)
    {
        var now = clock.GetCurrentInstant();
        var conv = new AgentConversation
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Locale = locale,
            StartedAt = now,
            LastMessageAt = now,
            MessageCount = 0
        };
        db.AgentConversations.Add(conv);
        await db.SaveChangesAsync(cancellationToken);
        return conv;
    }

    public async Task AppendMessageAsync(AgentMessage message, CancellationToken cancellationToken)
    {
        db.AgentMessages.Add(message);

        var conv = await db.AgentConversations.FirstAsync(c => c.Id == message.ConversationId, cancellationToken);
        conv.MessageCount += 1;
        conv.LastMessageAt = message.CreatedAt;

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AgentConversation>> ListConversationsForUserAsync(Guid userId, int take, CancellationToken cancellationToken) =>
        await db.AgentConversations
            .AsNoTracking()
            .Where(c => c.UserId == userId)
            // arch:db-sort-ok top-N conversation history selector
            .OrderByDescending(c => c.LastMessageAt)
            .Take(take)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<AgentConversation>> ListConversationsForUserWithMessagesAsync(Guid userId, CancellationToken cancellationToken) =>
        await db.AgentConversations
            .AsNoTracking()
            .Where(c => c.UserId == userId)
            // arch:db-sort-ok identity-ordered chronological message stream
            .Include(c => c.Messages.OrderBy(m => m.CreatedAt))
            // arch:db-sort-ok user conversation history chronology
            .OrderByDescending(c => c.LastMessageAt)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<AgentConversation>> ListAllConversationsAsync(
        bool refusalsOnly, Guid? userId, int take, int skip,
        CancellationToken cancellationToken)
    {
        IQueryable<AgentConversation> q = db.AgentConversations.AsNoTracking();

        if (userId is Guid u) q = q.Where(c => c.UserId == u);
        if (refusalsOnly) q = q.Where(c => c.Messages.Any(m => m.RefusalReason != null));

        // arch:db-sort-ok admin page window over conversation history
        return await q.OrderByDescending(c => c.LastMessageAt)
            .Skip(skip).Take(take)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AgentConversation>> ListAllConversationsWithMessagesAsync(
        bool refusalsOnly, bool handoffsOnly, Guid? userId, int take, int skip,
        CancellationToken cancellationToken)
    {
        IQueryable<AgentConversation> q = db.AgentConversations.AsNoTracking();

        if (userId is Guid u) q = q.Where(c => c.UserId == u);
        if (refusalsOnly) q = q.Where(c => c.Messages.Any(m => m.RefusalReason != null));
        if (handoffsOnly) q = q.Where(c => c.Messages.Any(m => m.HandedOffToFeedbackId != null));

        // arch:db-sort-ok pagination top-N by LastMessageAt
        return await q.OrderByDescending(c => c.LastMessageAt)
            // arch:db-sort-ok identity-ordered chronological message stream
            .Include(c => c.Messages.OrderBy(m => m.CreatedAt))
            .Skip(skip).Take(take)
            .ToListAsync(cancellationToken);
    }

    public async Task<int> PurgeConversationsOlderThanAsync(Instant cutoff, CancellationToken cancellationToken)
    {
        // Standard load+remove pattern (vs ExecuteDeleteAsync) — small scale,
        // retention runs once a day, and the in-memory provider used in unit
        // tests doesn't support ExecuteDeleteAsync.
        var stale = await db.AgentConversations
            .Where(c => c.LastMessageAt < cutoff)
            .ToListAsync(cancellationToken);
        if (stale.Count == 0) return 0;
        db.AgentConversations.RemoveRange(stale);
        await db.SaveChangesAsync(cancellationToken);
        return stale.Count;
    }

    // ---- Admin status -----------------------------------------------------

    public async Task<IReadOnlyList<AgentStatusMessageRow>> ListMessagesSinceAsync(
        Instant since, CancellationToken cancellationToken) =>
        await db.AgentMessages
            .AsNoTracking()
            .Where(m => m.CreatedAt >= since)
            // arch:db-sort-ok admin status window, identity-ordered chronology
            .OrderByDescending(m => m.CreatedAt)
            .Select(m => new AgentStatusMessageRow(
                m.ConversationId,
                m.Conversation.UserId,
                m.CreatedAt,
                m.PromptTokens,
                m.OutputTokens,
                m.CachedTokens,
                m.Model,
                m.DurationMs,
                m.FetchedDocs,
                m.RefusalReason))
            .ToListAsync(cancellationToken);

    public Task<int> CountConversationsInWindowAsync(
        Instant since, Instant until, CancellationToken cancellationToken) =>
        db.AgentConversations
            .AsNoTracking()
            .CountAsync(c => c.LastMessageAt >= since && c.LastMessageAt <= until, cancellationToken);
}
