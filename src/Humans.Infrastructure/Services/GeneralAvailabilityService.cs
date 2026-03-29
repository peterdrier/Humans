using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Humans.Infrastructure.Services;

public class GeneralAvailabilityService : IGeneralAvailabilityService
{
    private readonly HumansDbContext _dbContext;
    private readonly IClock _clock;

    public GeneralAvailabilityService(HumansDbContext dbContext, IClock clock)
    {
        _dbContext = dbContext;
        _clock = clock;
    }

    public async Task SetAvailabilityAsync(Guid userId, Guid eventSettingsId, List<int> dayOffsets)
    {
        var existing = await _dbContext.GeneralAvailability
            .FirstOrDefaultAsync(g => g.UserId == userId && g.EventSettingsId == eventSettingsId);

        var now = _clock.GetCurrentInstant();

        if (existing is not null)
        {
            existing.AvailableDayOffsets = dayOffsets;
            existing.UpdatedAt = now;
        }
        else
        {
            _dbContext.GeneralAvailability.Add(new GeneralAvailability
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                EventSettingsId = eventSettingsId,
                AvailableDayOffsets = dayOffsets,
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        await _dbContext.SaveChangesAsync();
    }

    public async Task<GeneralAvailability?> GetByUserAsync(Guid userId, Guid eventSettingsId)
    {
        return await _dbContext.GeneralAvailability
            .FirstOrDefaultAsync(g => g.UserId == userId && g.EventSettingsId == eventSettingsId);
    }

    public async Task<List<GeneralAvailability>> GetAvailableForDayAsync(Guid eventSettingsId, int dayOffset)
    {
        // EF Core can't translate List<int>.Contains inside a LINQ query for jsonb,
        // so we load all records for the event and filter in memory.
        // At ~500 users max this is fine.
        var all = await _dbContext.GeneralAvailability
            .Include(g => g.User)
            .Where(g => g.EventSettingsId == eventSettingsId)
            .ToListAsync();

        return all.Where(g => g.AvailableDayOffsets.Contains(dayOffset)).ToList();
    }

    public async Task DeleteAsync(Guid userId, Guid eventSettingsId)
    {
        var existing = await _dbContext.GeneralAvailability
            .FirstOrDefaultAsync(g => g.UserId == userId && g.EventSettingsId == eventSettingsId);

        if (existing is not null)
        {
            _dbContext.GeneralAvailability.Remove(existing);
            await _dbContext.SaveChangesAsync();
        }
    }
}
