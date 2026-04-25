using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using NodaTime.Testing;
using Humans.Application;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Infrastructure.Data;
using Xunit;
using Humans.Infrastructure.Repositories.Profiles;

namespace Humans.Application.Tests.Repositories;

public sealed class ProfileRepositoryTests : IDisposable
{
    private readonly HumansDbContext _dbContext;
    private readonly FakeClock _clock;
    private readonly ProfileRepository _repo;

    public ProfileRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _dbContext = new HumansDbContext(options);
        _clock = new FakeClock(Instant.FromUtc(2026, 3, 1, 12, 0));
        _repo = new ProfileRepository(new TestDbContextFactory(options), _clock);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    [HumansFact]
    public async Task ReconcileCVEntriesAsync_AddsUpdatesAndRemovesEntries()
    {
        // Arrange: profile with two existing CV entries
        var profileId = Guid.NewGuid();
        var keepId = Guid.NewGuid();
        var removeId = Guid.NewGuid();
        var now = _clock.GetCurrentInstant();

        await _dbContext.VolunteerHistoryEntries.AddRangeAsync(
            new VolunteerHistoryEntry
            {
                Id = keepId,
                ProfileId = profileId,
                Date = new LocalDate(2024, 3, 1),
                EventName = "Keep me",
                Description = "Old desc",
                CreatedAt = now,
                UpdatedAt = now
            },
            new VolunteerHistoryEntry
            {
                Id = removeId,
                ProfileId = profileId,
                Date = new LocalDate(2024, 4, 1),
                EventName = "Remove me",
                Description = null,
                CreatedAt = now,
                UpdatedAt = now
            });
        await _dbContext.SaveChangesAsync();

        // Advance clock so UpdatedAt on the updated entry differs from CreatedAt
        _clock.AdvanceSeconds(60);
        var afterAdvance = _clock.GetCurrentInstant();

        // Act: reconcile — keep one (new description), add one, remove "Remove me"
        var newEntries = new List<CVEntry>
        {
            new(keepId, new LocalDate(2024, 3, 1), "Keep me", "New desc"),
            new(Guid.Empty, new LocalDate(2024, 5, 1), "Add me", null),
        };
        await _repo.ReconcileCVEntriesAsync(profileId, newEntries, default);

        // Assert: exactly two rows remain. Use AsNoTracking so the query hits the in-memory
        // store directly rather than returning stale entities from _dbContext's identity map.
        var persisted = await _dbContext.VolunteerHistoryEntries
            .AsNoTracking()
            .Where(v => v.ProfileId == profileId)
            .OrderBy(v => v.Date)
            .ToListAsync();

        persisted.Should().HaveCount(2);

        // "Keep me" is updated with new description; "Remove me" is gone.
        // Id is preserved across the update.
        persisted[0].Id.Should().Be(keepId);
        persisted[0].EventName.Should().Be("Keep me");
        persisted[0].Description.Should().Be("New desc");
        persisted[0].UpdatedAt.Should().Be(afterAdvance);

        // "Add me" is new — fresh Id, not Guid.Empty
        persisted[1].Id.Should().NotBe(Guid.Empty);
        persisted[1].EventName.Should().Be("Add me");
        persisted[1].Description.Should().BeNull();
        persisted[1].CreatedAt.Should().Be(afterAdvance);
        persisted[1].UpdatedAt.Should().Be(afterAdvance);
    }

    [HumansFact]
    public async Task ReconcileCVEntriesAsync_DoesNotBumpUpdatedAt_WhenFieldsUnchanged()
    {
        var profileId = Guid.NewGuid();
        var entryId = Guid.NewGuid();
        var seededAt = _clock.GetCurrentInstant();

        await _dbContext.VolunteerHistoryEntries.AddAsync(new VolunteerHistoryEntry
        {
            Id = entryId,
            ProfileId = profileId,
            Date = new LocalDate(2024, 3, 1),
            EventName = "Keep me",
            Description = "unchanged",
            CreatedAt = seededAt,
            UpdatedAt = seededAt,
        });
        await _dbContext.SaveChangesAsync();

        // Advance the clock — if UpdatedAt were bumped unconditionally, we'd see the new time.
        _clock.AdvanceSeconds(60);

        var entries = new List<CVEntry>
        {
            new(entryId, new LocalDate(2024, 3, 1), "Keep me", "unchanged"),
        };
        await _repo.ReconcileCVEntriesAsync(profileId, entries, default);

        var persisted = await _dbContext.VolunteerHistoryEntries
            .AsNoTracking()
            .Where(v => v.ProfileId == profileId)
            .SingleAsync();
        persisted.UpdatedAt.Should().Be(seededAt);
    }

    [HumansFact]
    public async Task ReconcileCVEntriesAsync_PreservesIdAndCreatedAt_WhenDateOrEventNameChanges()
    {
        // The whole point of Id-keyed reconcile: editing Date or EventName
        // must update the existing row in place, not delete-and-insert (which
        // would lose Id and CreatedAt).
        var profileId = Guid.NewGuid();
        var entryId = Guid.NewGuid();
        var createdAt = _clock.GetCurrentInstant();

        await _dbContext.VolunteerHistoryEntries.AddAsync(new VolunteerHistoryEntry
        {
            Id = entryId,
            ProfileId = profileId,
            Date = new LocalDate(2024, 3, 1),
            EventName = "Original Name",
            Description = "desc",
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        });
        await _dbContext.SaveChangesAsync();

        _clock.AdvanceSeconds(60);
        var afterAdvance = _clock.GetCurrentInstant();

        // Mutate both Date and EventName but keep Id
        var entries = new List<CVEntry>
        {
            new(entryId, new LocalDate(2024, 6, 15), "Renamed Event", "desc"),
        };
        await _repo.ReconcileCVEntriesAsync(profileId, entries, default);

        var persisted = await _dbContext.VolunteerHistoryEntries
            .AsNoTracking()
            .Where(v => v.ProfileId == profileId)
            .SingleAsync();

        // Same row — Id and CreatedAt preserved
        persisted.Id.Should().Be(entryId);
        persisted.CreatedAt.Should().Be(createdAt);
        // New field values, UpdatedAt bumped
        persisted.Date.Should().Be(new LocalDate(2024, 6, 15));
        persisted.EventName.Should().Be("Renamed Event");
        persisted.UpdatedAt.Should().Be(afterAdvance);
    }
}
