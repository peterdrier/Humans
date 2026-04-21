using Humans.Application.DTOs.Calendar;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Infrastructure.Services;

public class CalendarService : ICalendarService
{
    private const string CacheKeyActiveEvents = "calendar:active-events";

    private readonly HumansDbContext _db;
    private readonly IMemoryCache _cache;
    private readonly IClock _clock;
    private readonly ILogger<CalendarService> _logger;

    public CalendarService(
        HumansDbContext db,
        IMemoryCache cache,
        IClock clock,
        ILogger<CalendarService> logger)
    {
        _db = db;
        _cache = cache;
        _clock = clock;
        _logger = logger;
    }

    public Task<IReadOnlyList<CalendarOccurrence>> GetOccurrencesInWindowAsync(
        Instant from, Instant to, Guid? teamId = null, CancellationToken ct = default)
        => throw new NotSupportedException("Not yet implemented.");

    public Task<CalendarEvent?> GetEventByIdAsync(Guid id, CancellationToken ct = default)
        => throw new NotSupportedException("Not yet implemented.");

    public Task<CalendarEvent> CreateEventAsync(CreateCalendarEventDto dto, Guid createdByUserId, CancellationToken ct = default)
        => throw new NotSupportedException("Not yet implemented.");

    public Task<CalendarEvent> UpdateEventAsync(Guid id, UpdateCalendarEventDto dto, Guid updatedByUserId, CancellationToken ct = default)
        => throw new NotSupportedException("Not yet implemented.");

    public Task DeleteEventAsync(Guid id, Guid deletedByUserId, CancellationToken ct = default)
        => throw new NotSupportedException("Not yet implemented.");

    public Task CancelOccurrenceAsync(Guid eventId, Instant originalOccurrenceStartUtc, Guid userId, CancellationToken ct = default)
        => throw new NotSupportedException("Not yet implemented.");

    public Task OverrideOccurrenceAsync(Guid eventId, Instant originalOccurrenceStartUtc, OverrideOccurrenceDto dto, Guid userId, CancellationToken ct = default)
        => throw new NotSupportedException("Not yet implemented.");
}
