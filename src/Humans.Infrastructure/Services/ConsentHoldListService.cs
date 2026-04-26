using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.AuditLog;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;

namespace Humans.Infrastructure.Services;

/// <summary>
/// Owns the <c>consent_hold_list</c> table. The AutoConsentCheckJob reads the
/// entire list on each run and passes it to the LLM assistant; at our scale
/// the list is expected to stay small (&lt; a few hundred entries) so no caching
/// is needed.
/// </summary>
public class ConsentHoldListService : IConsentHoldListService
{
    private readonly HumansDbContext _dbContext;
    private readonly IAuditLogService _auditLogService;
    private readonly IClock _clock;
    private readonly ILogger<ConsentHoldListService> _logger;

    public ConsentHoldListService(
        HumansDbContext dbContext,
        IAuditLogService auditLogService,
        IClock clock,
        ILogger<ConsentHoldListService> logger)
    {
        _dbContext = dbContext;
        _auditLogService = auditLogService;
        _clock = clock;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ConsentHoldListEntry>> ListAsync(CancellationToken ct = default)
    {
        return await _dbContext.ConsentHoldListEntries
            .AsNoTracking()
            .OrderByDescending(e => e.AddedAt)
            .ToListAsync(ct);
    }

    public async Task<ConsentHoldListEntry> AddAsync(
        string entry, string? note, Guid addedByUserId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(entry))
        {
            throw new ArgumentException("Entry text is required.", nameof(entry));
        }

        var trimmedEntry = entry.Trim();
        var trimmedNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();

        var newEntry = new ConsentHoldListEntry
        {
            Entry = trimmedEntry,
            Note = trimmedNote,
            AddedByUserId = addedByUserId,
            AddedAt = _clock.GetCurrentInstant(),
        };

        _dbContext.ConsentHoldListEntries.Add(newEntry);

        await _auditLogService.LogAsync(
            AuditAction.ConsentHoldListEntryAdded,
            nameof(ConsentHoldListEntry),
            // Entity id isn't known yet (int auto-increment); use Guid.Empty
            // placeholder for the GUID-typed audit FK. The description carries
            // the actual text — which is what reviewers look at anyway.
            Guid.Empty,
            $"Added hold-list entry: {trimmedEntry}" +
                (trimmedNote is null ? string.Empty : $" (note: {trimmedNote})"),
            addedByUserId);

        await _dbContext.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Admin {AdminId} added hold-list entry {EntryId}: {Entry}",
            addedByUserId, newEntry.Id, trimmedEntry);

        return newEntry;
    }

    public async Task DeleteAsync(int id, Guid actingUserId, CancellationToken ct = default)
    {
        var entry = await _dbContext.ConsentHoldListEntries
            .FirstOrDefaultAsync(e => e.Id == id, ct);
        if (entry is null)
        {
            _logger.LogInformation(
                "DeleteAsync called for non-existent hold-list entry {EntryId} by {ActorId}",
                id, actingUserId);
            return;
        }

        var removedText = entry.Entry;
        _dbContext.ConsentHoldListEntries.Remove(entry);

        await _auditLogService.LogAsync(
            AuditAction.ConsentHoldListEntryRemoved,
            nameof(ConsentHoldListEntry),
            Guid.Empty,
            $"Removed hold-list entry: {removedText}",
            actingUserId);

        await _dbContext.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Admin {AdminId} removed hold-list entry {EntryId}: {Entry}",
            actingUserId, id, removedText);
    }
}
