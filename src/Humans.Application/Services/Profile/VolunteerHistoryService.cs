using NodaTime;
using Humans.Application.DTOs;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;

namespace Humans.Application.Services.Profile;

/// <summary>
/// Service for managing volunteer history entries. Business logic only —
/// cache/store updates are handled by the <c>CachingVolunteerHistoryService</c>
/// decorator in the Infrastructure layer.
/// </summary>
public sealed class VolunteerHistoryService : IVolunteerHistoryService
{
    private readonly IVolunteerHistoryRepository _repository;
    private readonly IClock _clock;

    public VolunteerHistoryService(
        IVolunteerHistoryRepository repository,
        IClock clock)
    {
        _repository = repository;
        _clock = clock;
    }

    public async Task<IReadOnlyList<VolunteerHistoryEntryDto>> GetAllAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        var entries = await _repository.GetByProfileIdReadOnlyAsync(profileId, cancellationToken);

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

        // Get existing entries (tracked for in-place mutation)
        var existingEntries = await _repository.GetByProfileIdTrackedAsync(profileId, cancellationToken);

        var existingById = existingEntries.ToDictionary(v => v.Id);
        var incomingIds = entries.Where(e => e.Id.HasValue).Select(e => e.Id!.Value).ToHashSet();

        // Entries to delete (no longer in incoming set)
        var toDelete = existingEntries.Where(v => !incomingIds.Contains(v.Id)).ToList();

        // Entries to add (new, no existing ID)
        var toAdd = new List<VolunteerHistoryEntry>();

        foreach (var dto in entries)
        {
            if (dto.Id.HasValue && existingById.TryGetValue(dto.Id.Value, out var existing))
            {
                // Update existing (in-place mutation on tracked entity)
                existing.Date = dto.Date;
                existing.EventName = dto.EventName;
                existing.Description = dto.Description;
                existing.UpdatedAt = now;
            }
            else
            {
                // Create new
                toAdd.Add(new VolunteerHistoryEntry
                {
                    Id = Guid.NewGuid(),
                    ProfileId = profileId,
                    Date = dto.Date,
                    EventName = dto.EventName,
                    Description = dto.Description,
                    CreatedAt = now,
                    UpdatedAt = now
                });
            }
        }

        await _repository.BatchSaveAsync(toAdd, toDelete, cancellationToken);
    }
}
