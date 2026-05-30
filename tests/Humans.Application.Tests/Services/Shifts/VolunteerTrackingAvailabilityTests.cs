using AwesomeAssertions;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Shifts;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Infrastructure.Repositories.Shifts;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using NSubstitute;

namespace Humans.Application.Tests.Services.Shifts;

public sealed class VolunteerTrackingAvailabilityTests : ServiceTestHarness
{
    private readonly VolunteerTrackingRepository _repo;
    private readonly VolunteerTrackingService _service;

    private static readonly Instant TestNow = Instant.FromUtc(2026, 6, 15, 12, 0);

    public VolunteerTrackingAvailabilityTests()
        : base(TestNow)
    {
        _repo = new VolunteerTrackingRepository(Db);
        _service = new VolunteerTrackingService(
            _repo,
            Substitute.For<IShiftManagementRepository>(),
            Substitute.For<IUserService>(),
            Substitute.For<IShiftViewInvalidator>(),
            Clock);
    }

    [HumansFact(Timeout = 10000)]
    public async Task SetAvailability_CreatesRecord()
    {
        var userId = Guid.NewGuid();
        var esId = SeedEventSettings();
        await Db.SaveChangesAsync();

        await _service.SetAvailabilityAsync(userId, esId, [-3, -2, -1]);

        var record = await Db.GeneralAvailability
            .AsNoTracking()
            .FirstOrDefaultAsync(g => g.UserId == userId && g.EventSettingsId == esId);
        record.Should().NotBeNull();
        record.AvailableDayOffsets.Should().BeEquivalentTo([-3, -2, -1]);
    }

    [HumansFact]
    public async Task SetAvailability_UpdatesExistingRecord()
    {
        var userId = Guid.NewGuid();
        var esId = SeedEventSettings();
        await Db.SaveChangesAsync();

        // First set
        await _service.SetAvailabilityAsync(userId, esId, [-3, -2]);

        // Update
        await _service.SetAvailabilityAsync(userId, esId, [0, 1, 2]);

        var records = await Db.GeneralAvailability
            .AsNoTracking()
            .Where(g => g.UserId == userId && g.EventSettingsId == esId)
            .ToListAsync();
        records.Should().HaveCount(1);
        records[0].AvailableDayOffsets.Should().BeEquivalentTo([0, 1, 2]);
    }

    [HumansFact]
    public async Task GetAvailableVolunteers_ReturnsMatchingDayOffset()
    {
        var user1Id = Guid.NewGuid();
        var user2Id = Guid.NewGuid();
        var user3Id = Guid.NewGuid();
        var esId = SeedEventSettings();
        await Db.SaveChangesAsync();

        await _service.SetAvailabilityAsync(user1Id, esId, [-3, -2, -1]);
        await _service.SetAvailabilityAsync(user2Id, esId, [-2, 0, 1]);
        await _service.SetAvailabilityAsync(user3Id, esId, [0, 1, 2]);

        // Query for day -2: should return user1 and user2
        var available = await _service.GetAvailableForDayAsync(esId, -2);
        available.Should().HaveCount(2);
        available.Select(a => a.UserId).Should().BeEquivalentTo([user1Id, user2Id]);
    }

    [HumansFact]
    public async Task SetDayAvailability_AddsOffset_PreservingExisting()
    {
        var userId = Guid.NewGuid();
        var esId = SeedEventSettings();
        await Db.SaveChangesAsync();
        await _service.SetAvailabilityAsync(userId, esId, [-3]);

        var changed = await _service.SetDayAvailabilityAsync(
            userId, esId, -2, true);

        changed.Should().BeTrue();
        var rec = await Db.GeneralAvailability.AsNoTracking()
            .FirstAsync(g => g.UserId == userId && g.EventSettingsId == esId);
        rec.AvailableDayOffsets.Should().BeEquivalentTo([-3, -2]);
    }

    [HumansFact]
    public async Task SetDayAvailability_RemovesOffset()
    {
        var userId = Guid.NewGuid();
        var esId = SeedEventSettings();
        await Db.SaveChangesAsync();
        await _service.SetAvailabilityAsync(userId, esId, [-3, -2]);

        var changed = await _service.SetDayAvailabilityAsync(
            userId, esId, -2, false);

        changed.Should().BeTrue();
        var rec = await Db.GeneralAvailability.AsNoTracking()
            .FirstAsync(g => g.UserId == userId && g.EventSettingsId == esId);
        rec.AvailableDayOffsets.Should().BeEquivalentTo([-3]);
    }

    [HumansFact]
    public async Task SetDayAvailability_CreatesRowWhenNoneExists()
    {
        var userId = Guid.NewGuid();
        var esId = SeedEventSettings();
        await Db.SaveChangesAsync();

        var changed = await _service.SetDayAvailabilityAsync(
            userId, esId, -1, true);

        changed.Should().BeTrue();
        var rec = await Db.GeneralAvailability.AsNoTracking()
            .FirstOrDefaultAsync(g => g.UserId == userId && g.EventSettingsId == esId);
        rec.Should().NotBeNull();
        rec!.AvailableDayOffsets.Should().BeEquivalentTo([-1]);
    }

    [HumansFact]
    public async Task SetDayAvailability_RejectsPositiveOffset()
    {
        var userId = Guid.NewGuid();
        var esId = SeedEventSettings();
        await Db.SaveChangesAsync();

        var changed = await _service.SetDayAvailabilityAsync(
            userId, esId, 2, true);

        changed.Should().BeFalse();
        var rec = await Db.GeneralAvailability.AsNoTracking()
            .FirstOrDefaultAsync(g => g.UserId == userId && g.EventSettingsId == esId);
        rec.Should().BeNull(); // no-op, no row created
    }

    [HumansFact]
    public async Task SetDayAvailability_NoOp_WhenAddingAlreadyPresentOffset()
    {
        var userId = Guid.NewGuid();
        var esId = SeedEventSettings();
        await Db.SaveChangesAsync();
        await _service.SetAvailabilityAsync(userId, esId, [-2]);

        var changed = await _service.SetDayAvailabilityAsync(
            userId, esId, -2, true);

        changed.Should().BeFalse();
    }

    [HumansFact]
    public async Task SetDayAvailability_NoOp_WhenRemovingAbsentOffset()
    {
        var userId = Guid.NewGuid();
        var esId = SeedEventSettings();
        await Db.SaveChangesAsync();
        await _service.SetAvailabilityAsync(userId, esId, [-3]);

        var changed = await _service.SetDayAvailabilityAsync(
            userId, esId, -2, false);

        changed.Should().BeFalse();
    }

    private Guid SeedEventSettings()
    {
        var es = new EventSettings
        {
            Id = Guid.NewGuid(),
            EventName = "Test Event",
            TimeZoneId = "Europe/Madrid",
            GateOpeningDate = new LocalDate(2026, 7, 1),
            BuildStartOffset = -14,
            EventEndOffset = 6,
            StrikeEndOffset = 9,
            IsActive = true,
            CreatedAt = TestNow,
            UpdatedAt = TestNow
        };
        Db.EventSettings.Add(es);
        return es.Id;
    }
}
