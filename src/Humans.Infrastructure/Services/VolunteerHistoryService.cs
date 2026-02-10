using Microsoft.EntityFrameworkCore;
using NodaTime;
using Humans.Application.DTOs;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Infrastructure.Data;

namespace Humans.Infrastructure.Services;

/// <summary>
/// Service for managing volunteer history entries.
/// </summary>
public class VolunteerHistoryService : IVolunteerHistoryService
{
    private readonly HumansDbContext _dbContext;
    private readonly IClock _clock;

    public VolunteerHistoryService(HumansDbContext dbContext, IClock clock)
    {
        _dbContext = dbContext;
        _clock = clock;
    }

    public async Task<IReadOnlyList<VolunteerHistoryEntryDto>> GetAllAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        var entries = await _dbContext.VolunteerHistoryEntries
            .AsNoTracking()
            .Where(v => v.ProfileId == profileId)
            .OrderByDescending(v => v.Date)
            .ThenByDescending(v => v.CreatedAt)
            .ToListAsync(cancellationToken);

        return entries.Select(v => new VolunteerHistoryEntryDto(
            v.Id,
            v.Date,
            v.EventName,
            v.Description
        )).ToList();
    }

    public async Task SaveAsync(
        Guid profileId,
        IReadOnlyList<VolunteerHistoryEntryEditDto> entries,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.GetCurrentInstant();

        // Get existing entries
        var existingEntries = await _dbContext.VolunteerHistoryEntries
            .Where(v => v.ProfileId == profileId)
            .ToListAsync(cancellationToken);

        var existingById = existingEntries.ToDictionary(v => v.Id);
        var incomingIds = entries.Where(e => e.Id.HasValue).Select(e => e.Id!.Value).ToHashSet();

        // Delete entries that are no longer present
        var toDelete = existingEntries.Where(v => !incomingIds.Contains(v.Id)).ToList();
        _dbContext.VolunteerHistoryEntries.RemoveRange(toDelete);

        // Update or create entries
        foreach (var dto in entries)
        {
            if (dto.Id.HasValue && existingById.TryGetValue(dto.Id.Value, out var existing))
            {
                // Update existing
                existing.Date = dto.Date;
                existing.EventName = dto.EventName;
                existing.Description = dto.Description;
                existing.UpdatedAt = now;
            }
            else
            {
                // Create new
                var newEntry = new VolunteerHistoryEntry
                {
                    Id = Guid.NewGuid(),
                    ProfileId = profileId,
                    Date = dto.Date,
                    EventName = dto.EventName,
                    Description = dto.Description,
                    CreatedAt = now,
                    UpdatedAt = now
                };
                _dbContext.VolunteerHistoryEntries.Add(newEntry);
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
