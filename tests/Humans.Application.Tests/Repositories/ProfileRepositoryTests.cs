using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using NodaTime.Testing;
using Humans.Application;
using Humans.Domain.Entities;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Repositories;
using Xunit;

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
        _repo = new ProfileRepository(_dbContext, _clock);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task ReconcileCVEntriesAsync_AddsUpdatesAndRemovesEntries()
    {
        // Arrange: profile with two existing CV entries
        var profileId = Guid.NewGuid();
        var now = _clock.GetCurrentInstant();

        await _dbContext.VolunteerHistoryEntries.AddRangeAsync(
            new VolunteerHistoryEntry
            {
                Id = Guid.NewGuid(),
                ProfileId = profileId,
                Date = new LocalDate(2024, 3, 1),
                EventName = "Keep me",
                Description = "Old desc",
                CreatedAt = now,
                UpdatedAt = now
            },
            new VolunteerHistoryEntry
            {
                Id = Guid.NewGuid(),
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
            new(new LocalDate(2024, 3, 1), "Keep me", "New desc"),
            new(new LocalDate(2024, 5, 1), "Add me", null),
        };
        await _repo.ReconcileCVEntriesAsync(profileId, newEntries, default);

        // Assert: exactly two rows remain
        var persisted = await _dbContext.VolunteerHistoryEntries
            .Where(v => v.ProfileId == profileId)
            .OrderBy(v => v.Date)
            .ToListAsync();

        persisted.Should().HaveCount(2);

        // "Keep me" is updated with new description; "Remove me" is gone
        persisted[0].EventName.Should().Be("Keep me");
        persisted[0].Description.Should().Be("New desc");
        persisted[0].UpdatedAt.Should().Be(afterAdvance);

        // "Add me" is new
        persisted[1].EventName.Should().Be("Add me");
        persisted[1].Description.Should().BeNull();
        persisted[1].CreatedAt.Should().Be(afterAdvance);
        persisted[1].UpdatedAt.Should().Be(afterAdvance);
    }

    [Fact]
    public async Task ReconcileCVEntriesAsync_DoesNotBumpUpdatedAt_WhenDescriptionUnchanged()
    {
        var profileId = Guid.NewGuid();
        var seededAt = _clock.GetCurrentInstant();

        await _dbContext.VolunteerHistoryEntries.AddAsync(new VolunteerHistoryEntry
        {
            Id = Guid.NewGuid(),
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
            new(new LocalDate(2024, 3, 1), "Keep me", "unchanged"),
        };
        await _repo.ReconcileCVEntriesAsync(profileId, entries, default);

        var persisted = await _dbContext.VolunteerHistoryEntries
            .Where(v => v.ProfileId == profileId)
            .SingleAsync();
        persisted.UpdatedAt.Should().Be(seededAt);
    }

    [Fact]
    public async Task ReconcileCVEntriesAsync_CollapsesPreExistingDuplicates()
    {
        // Models pre-Phase-10 data: the old Guid-keyed writer allowed two rows
        // with the same (Date, EventName). The new key scheme treats those as
        // a single entry; the first reconcile must collapse the group down to
        // one row without throwing.
        var profileId = Guid.NewGuid();
        var now = _clock.GetCurrentInstant();

        await _dbContext.VolunteerHistoryEntries.AddRangeAsync(
            new VolunteerHistoryEntry
            {
                Id = Guid.NewGuid(),
                ProfileId = profileId,
                Date = new LocalDate(2024, 3, 1),
                EventName = "Dup",
                Description = "first",
                CreatedAt = now,
                UpdatedAt = now,
            },
            new VolunteerHistoryEntry
            {
                Id = Guid.NewGuid(),
                ProfileId = profileId,
                Date = new LocalDate(2024, 3, 1),
                EventName = "Dup",
                Description = "second",
                CreatedAt = now,
                UpdatedAt = now,
            });
        await _dbContext.SaveChangesAsync();

        // Reconcile with the same single entry — duplicates should collapse.
        var entries = new List<CVEntry>
        {
            new(new LocalDate(2024, 3, 1), "Dup", "first"),
        };
        await _repo.ReconcileCVEntriesAsync(profileId, entries, default);

        var persisted = await _dbContext.VolunteerHistoryEntries
            .Where(v => v.ProfileId == profileId)
            .ToListAsync();
        persisted.Should().ContainSingle();
        persisted[0].Description.Should().Be("first");
    }
}
