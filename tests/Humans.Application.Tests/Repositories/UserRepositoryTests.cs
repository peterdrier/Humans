using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using NodaTime.Testing;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Xunit;
using Humans.Infrastructure.Repositories.Users;

namespace Humans.Application.Tests.Repositories;

public sealed class UserRepositoryTests : IDisposable
{
    private readonly HumansDbContext _dbContext;
    private readonly FakeClock _clock;
    private readonly UserRepository _repo;

    public UserRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _dbContext = new HumansDbContext(options);
        _clock = new FakeClock(Instant.FromUtc(2026, 3, 1, 12, 0));
        _repo = new UserRepository(new TestDbContextFactory(options));
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<User> SeedUserAsync(string? googleEmail = null)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            UserName = $"user-{Guid.NewGuid():N}@example.com",
            Email = $"user-{Guid.NewGuid():N}@example.com",
            DisplayName = "Seeded User",
            CreatedAt = _clock.GetCurrentInstant(),
            GoogleEmail = googleEmail,
        };
        _dbContext.Users.Add(user);
        await _dbContext.SaveChangesAsync();
        return user;
    }

    // ==========================================================================
    // TrySetGoogleEmailAsync
    // ==========================================================================

    [HumansFact]
    public async Task TrySetGoogleEmailAsync_ReturnsFalse_WhenUserDoesNotExist()
    {
        var result = await _repo.TrySetGoogleEmailAsync(
            Guid.NewGuid(), "new@example.com", default);

        result.Should().BeFalse();
    }

    [HumansFact]
    public async Task TrySetGoogleEmailAsync_SetsValueAndReturnsTrue_WhenGoogleEmailIsNull()
    {
        var user = await SeedUserAsync(googleEmail: null);

        var result = await _repo.TrySetGoogleEmailAsync(user.Id, "new@nobodies.team", default);

        result.Should().BeTrue();
        var reloaded = await _dbContext.Users.AsNoTracking().FirstAsync(u => u.Id == user.Id);
        reloaded.GoogleEmail.Should().Be("new@nobodies.team");
    }

    [HumansFact]
    public async Task TrySetGoogleEmailAsync_DoesNotOverwrite_WhenGoogleEmailAlreadySet()
    {
        var user = await SeedUserAsync(googleEmail: "existing@nobodies.team");

        var result = await _repo.TrySetGoogleEmailAsync(user.Id, "new@nobodies.team", default);

        result.Should().BeFalse();
        var reloaded = await _dbContext.Users.AsNoTracking().FirstAsync(u => u.Id == user.Id);
        reloaded.GoogleEmail.Should().Be("existing@nobodies.team");
    }

    // ==========================================================================
    // UpdateDisplayNameAsync
    // ==========================================================================

    [HumansFact]
    public async Task UpdateDisplayNameAsync_ReturnsFalse_WhenUserDoesNotExist()
    {
        var result = await _repo.UpdateDisplayNameAsync(Guid.NewGuid(), "Nobody", default);
        result.Should().BeFalse();
    }

    [HumansFact]
    public async Task UpdateDisplayNameAsync_UpdatesAndReturnsTrue_WhenUserExists()
    {
        var user = await SeedUserAsync();

        var result = await _repo.UpdateDisplayNameAsync(user.Id, "Renamed Person", default);

        result.Should().BeTrue();
        var reloaded = await _dbContext.Users.AsNoTracking().FirstAsync(u => u.Id == user.Id);
        reloaded.DisplayName.Should().Be("Renamed Person");
    }

    // ==========================================================================
    // SetDeletionPendingAsync / ClearDeletionAsync
    // ==========================================================================

    [HumansFact]
    public async Task SetDeletionPendingAsync_SetsBothFieldsAndReturnsTrue()
    {
        var user = await SeedUserAsync();
        var requested = Instant.FromUtc(2026, 4, 1, 0, 0);
        var scheduled = Instant.FromUtc(2026, 5, 1, 0, 0);

        var result = await _repo.SetDeletionPendingAsync(user.Id, requested, scheduled, default);

        result.Should().BeTrue();
        var reloaded = await _dbContext.Users.AsNoTracking().FirstAsync(u => u.Id == user.Id);
        reloaded.DeletionRequestedAt.Should().Be(requested);
        reloaded.DeletionScheduledFor.Should().Be(scheduled);
    }

    [HumansFact]
    public async Task ClearDeletionAsync_ClearsAllDeletionFieldsIncludingEligibleAfter()
    {
        var user = await SeedUserAsync();
        user.DeletionRequestedAt = Instant.FromUtc(2026, 4, 1, 0, 0);
        user.DeletionScheduledFor = Instant.FromUtc(2026, 5, 1, 0, 0);
        user.DeletionEligibleAfter = Instant.FromUtc(2026, 6, 1, 0, 0);
        await _dbContext.SaveChangesAsync();

        var result = await _repo.ClearDeletionAsync(user.Id, default);

        result.Should().BeTrue();
        var reloaded = await _dbContext.Users.AsNoTracking().FirstAsync(u => u.Id == user.Id);
        reloaded.DeletionRequestedAt.Should().BeNull();
        reloaded.DeletionScheduledFor.Should().BeNull();
        reloaded.DeletionEligibleAfter.Should().BeNull();
    }

    // ==========================================================================
    // UpsertParticipationAsync
    // ==========================================================================

    [HumansFact]
    public async Task UpsertParticipationAsync_CreatesNewRecord_WhenNoneExists()
    {
        var userId = Guid.NewGuid();
        var now = _clock.GetCurrentInstant();

        var result = await _repo.UpsertParticipationAsync(
            userId, 2026, ParticipationStatus.NotAttending, ParticipationSource.UserDeclared, now, default);

        result.Should().NotBeNull();
        result!.Status.Should().Be(ParticipationStatus.NotAttending);
        result.Source.Should().Be(ParticipationSource.UserDeclared);
        result.DeclaredAt.Should().Be(now);

        var persisted = await _dbContext.EventParticipations.AsNoTracking()
            .FirstAsync(ep => ep.UserId == userId && ep.Year == 2026);
        persisted.Id.Should().Be(result.Id);
    }

    [HumansFact]
    public async Task UpsertParticipationAsync_UpdatesExistingNonAttendedRecord()
    {
        var userId = Guid.NewGuid();
        _dbContext.EventParticipations.Add(new EventParticipation
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Year = 2026,
            Status = ParticipationStatus.NotAttending,
            Source = ParticipationSource.UserDeclared,
            DeclaredAt = _clock.GetCurrentInstant(),
        });
        await _dbContext.SaveChangesAsync();

        var result = await _repo.UpsertParticipationAsync(
            userId, 2026, ParticipationStatus.Ticketed, ParticipationSource.TicketSync, null, default);

        result.Should().NotBeNull();
        result!.Status.Should().Be(ParticipationStatus.Ticketed);
        result.Source.Should().Be(ParticipationSource.TicketSync);
        result.DeclaredAt.Should().BeNull();
    }

    [HumansFact]
    public async Task UpsertParticipationAsync_IsNoOp_WhenExistingStatusIsAttended()
    {
        var userId = Guid.NewGuid();
        var attendedAt = _clock.GetCurrentInstant();
        _dbContext.EventParticipations.Add(new EventParticipation
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Year = 2026,
            Status = ParticipationStatus.Attended,
            Source = ParticipationSource.TicketSync,
            DeclaredAt = attendedAt,
        });
        await _dbContext.SaveChangesAsync();

        var result = await _repo.UpsertParticipationAsync(
            userId, 2026, ParticipationStatus.NotAttending, ParticipationSource.UserDeclared, _clock.GetCurrentInstant(), default);

        result.Should().BeNull();
        var persisted = await _dbContext.EventParticipations.AsNoTracking()
            .FirstAsync(ep => ep.UserId == userId && ep.Year == 2026);
        persisted.Status.Should().Be(ParticipationStatus.Attended);
        persisted.Source.Should().Be(ParticipationSource.TicketSync);
    }

    // ==========================================================================
    // RemoveParticipationAsync
    // ==========================================================================

    [HumansFact]
    public async Task RemoveParticipationAsync_RemovesWhenSourceMatchesAndStatusNotAttended()
    {
        var userId = Guid.NewGuid();
        _dbContext.EventParticipations.Add(new EventParticipation
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Year = 2026,
            Status = ParticipationStatus.Ticketed,
            Source = ParticipationSource.TicketSync,
        });
        await _dbContext.SaveChangesAsync();

        var result = await _repo.RemoveParticipationAsync(
            userId, 2026, ParticipationSource.TicketSync, default);

        result.Should().BeTrue();
        var remaining = await _dbContext.EventParticipations.AsNoTracking()
            .CountAsync(ep => ep.UserId == userId);
        remaining.Should().Be(0);
    }

    [HumansFact]
    public async Task RemoveParticipationAsync_DoesNotRemove_WhenSourceMismatch()
    {
        var userId = Guid.NewGuid();
        _dbContext.EventParticipations.Add(new EventParticipation
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Year = 2026,
            Status = ParticipationStatus.NotAttending,
            Source = ParticipationSource.UserDeclared,
        });
        await _dbContext.SaveChangesAsync();

        var result = await _repo.RemoveParticipationAsync(
            userId, 2026, ParticipationSource.TicketSync, default);

        result.Should().BeFalse();
        var remaining = await _dbContext.EventParticipations.AsNoTracking()
            .CountAsync(ep => ep.UserId == userId);
        remaining.Should().Be(1);
    }

    [HumansFact]
    public async Task RemoveParticipationAsync_DoesNotRemove_WhenStatusIsAttended()
    {
        var userId = Guid.NewGuid();
        _dbContext.EventParticipations.Add(new EventParticipation
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Year = 2026,
            Status = ParticipationStatus.Attended,
            Source = ParticipationSource.TicketSync,
        });
        await _dbContext.SaveChangesAsync();

        var result = await _repo.RemoveParticipationAsync(
            userId, 2026, ParticipationSource.TicketSync, default);

        result.Should().BeFalse();
        var remaining = await _dbContext.EventParticipations.AsNoTracking()
            .CountAsync(ep => ep.UserId == userId);
        remaining.Should().Be(1);
    }

    // ==========================================================================
    // BackfillParticipationsAsync
    // ==========================================================================

    [HumansFact]
    public async Task BackfillParticipationsAsync_AddsNewAndUpdatesExistingNonAttended_PreservingAttended()
    {
        var userNewId = Guid.NewGuid();
        var userExistingId = Guid.NewGuid();
        var userAttendedId = Guid.NewGuid();

        await _dbContext.EventParticipations.AddRangeAsync(
            new EventParticipation
            {
                Id = Guid.NewGuid(),
                UserId = userExistingId,
                Year = 2025,
                Status = ParticipationStatus.NotAttending,
                Source = ParticipationSource.UserDeclared,
            },
            new EventParticipation
            {
                Id = Guid.NewGuid(),
                UserId = userAttendedId,
                Year = 2025,
                Status = ParticipationStatus.Attended,
                Source = ParticipationSource.TicketSync,
            });
        await _dbContext.SaveChangesAsync();

        var entries = new List<(Guid UserId, ParticipationStatus Status)>
        {
            (userNewId, ParticipationStatus.Ticketed),
            (userExistingId, ParticipationStatus.Ticketed),
            (userAttendedId, ParticipationStatus.NotAttending),
        };

        var count = await _repo.BackfillParticipationsAsync(2025, entries, default);

        count.Should().Be(3);

        var records = await _dbContext.EventParticipations.AsNoTracking()
            .Where(ep => ep.Year == 2025)
            .ToListAsync();
        records.Should().HaveCount(3);

        var newRecord = records.Single(r => r.UserId == userNewId);
        newRecord.Status.Should().Be(ParticipationStatus.Ticketed);
        newRecord.Source.Should().Be(ParticipationSource.AdminBackfill);

        var updatedRecord = records.Single(r => r.UserId == userExistingId);
        updatedRecord.Status.Should().Be(ParticipationStatus.Ticketed);
        updatedRecord.Source.Should().Be(ParticipationSource.AdminBackfill);

        var attendedRecord = records.Single(r => r.UserId == userAttendedId);
        attendedRecord.Status.Should().Be(ParticipationStatus.Attended);
        attendedRecord.Source.Should().Be(ParticipationSource.TicketSync);
    }

    // Note: GetByEmailOrAlternateAsync is not unit-tested — its matching uses
    // EF.Functions.ILike which is a Npgsql-specific translation and does not
    // evaluate against the InMemory provider. Behavior is verified end-to-end
    // in preview/QA against Postgres. The alternate-email computation (gmail
    // ↔ googlemail) is in UserService and is tested there.
}
