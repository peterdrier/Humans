using AwesomeAssertions;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Services.Shifts;
using Humans.Domain.Entities;
using NSubstitute;

namespace Humans.Application.Tests.Services.Shifts;

/// <summary>
/// Tests for the undecorated inner <see cref="ShiftViewService"/>: empty-view
/// behavior for unknown ids / no active event, and the rota fan-out
/// (Shifts + Tags + flattened Signups). Issue #720.
/// </summary>
public class ShiftViewServiceTests
{
    private readonly IShiftManagementRepository _management = Substitute.For<IShiftManagementRepository>();
    private readonly IShiftSignupRepository _signups = Substitute.For<IShiftSignupRepository>();
    private readonly IVolunteerTrackingRepository _tracking = Substitute.For<IVolunteerTrackingRepository>();

    private ShiftViewService CreateSut() =>
        new(_management, _signups, _tracking);

    [HumansFact]
    public async Task GetUserAsync_NoActiveEvent_ReturnsEmptyAvailabilityAndBuildStatus()
    {
        var userId = Guid.NewGuid();
        _management.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>())
            .Returns((EventSettings?)null);
        _management.GetVolunteerEventProfileAsync(userId, Arg.Any<CancellationToken>())
            .Returns((VolunteerEventProfile?)null);
        _signups.GetVolunteerTagPreferencesForUserAsync(userId, Arg.Any<CancellationToken>())
            .Returns([]);
        _signups.GetByUserAsync(userId, Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var sut = CreateSut();
        var view = await sut.GetUserAsync(userId);

        view.Should().NotBeNull();
        view.UserId.Should().Be(userId);
        view.Profile.Should().BeNull();
        view.Availability.Should().BeNull();
        view.BuildStatus.Should().BeNull();
        view.TagPreferences.Should().BeEmpty();
        view.Signups.Should().BeEmpty();
    }

    [HumansFact]
    public async Task GetUserAsync_WithActiveEvent_LoadsEventScopedAvailabilityAndBuildStatus()
    {
        var userId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var es = new EventSettings { Id = eventId, IsActive = true };
        var availability = new GeneralAvailability { UserId = userId, EventSettingsId = eventId };
        var buildStatus = new VolunteerBuildStatus { UserId = userId, EventSettingsId = eventId };

        _management.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>()).Returns(es);
        _management.GetVolunteerEventProfileAsync(userId, Arg.Any<CancellationToken>())
            .Returns((VolunteerEventProfile?)null);
        _signups.GetVolunteerTagPreferencesForUserAsync(userId, Arg.Any<CancellationToken>())
            .Returns([]);
        _signups.GetByUserAsync(userId, Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _tracking.GetAvailabilityByUserAndEventAsync(userId, eventId, Arg.Any<CancellationToken>())
            .Returns(availability);
        _tracking.GetAsync(userId, eventId, Arg.Any<CancellationToken>())
            .Returns(buildStatus);

        var sut = CreateSut();
        var view = await sut.GetUserAsync(userId);

        view.Availability.Should().BeSameAs(availability);
        view.BuildStatus.Should().BeSameAs(buildStatus);
    }

    [HumansFact]
    public async Task GetUserAsync_NoActiveEvent_DoesNotQuerySignups()
    {
        var userId = Guid.NewGuid();
        _management.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>())
            .Returns((EventSettings?)null);
        _management.GetVolunteerEventProfileAsync(userId, Arg.Any<CancellationToken>())
            .Returns((VolunteerEventProfile?)null);
        _signups.GetVolunteerTagPreferencesForUserAsync(userId, Arg.Any<CancellationToken>())
            .Returns([]);

        var sut = CreateSut();
        var view = await sut.GetUserAsync(userId);

        view.Signups.Should().BeEmpty();
        await _signups.DidNotReceive().GetByUserAsync(
            Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task GetUserAsync_WithActiveEvent_ScopesSignupsToActiveEventId()
    {
        var userId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var es = new EventSettings { Id = eventId, IsActive = true };
        var scoped = new[] { new ShiftSignup { Id = Guid.NewGuid(), UserId = userId } };

        _management.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>()).Returns(es);
        _management.GetVolunteerEventProfileAsync(userId, Arg.Any<CancellationToken>())
            .Returns((VolunteerEventProfile?)null);
        _signups.GetVolunteerTagPreferencesForUserAsync(userId, Arg.Any<CancellationToken>())
            .Returns([]);
        _signups.GetByUserAsync(userId, eventId, Arg.Any<CancellationToken>()).Returns(scoped);

        var sut = CreateSut();
        var view = await sut.GetUserAsync(userId);

        view.Signups.Should().BeSameAs(scoped);
        await _signups.Received(1).GetByUserAsync(userId, eventId, Arg.Any<CancellationToken>());
        await _signups.DidNotReceive().GetByUserAsync(
            userId, null, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task GetRotaAsync_UnknownRotaId_ReturnsEmptyView()
    {
        var rotaId = Guid.NewGuid();
        _management.GetRotaForViewAsync(rotaId, Arg.Any<CancellationToken>())
            .Returns((Rota?)null);

        var sut = CreateSut();
        var view = await sut.GetRotaAsync(rotaId);

        view.Should().NotBeNull();
        view.RotaId.Should().Be(rotaId);
        view.Rota.Should().BeNull();
        view.Shifts.Should().BeEmpty();
        view.Tags.Should().BeEmpty();
        view.Signups.Should().BeEmpty();
    }

    [HumansFact]
    public async Task GetRotaAsync_FlattensSignupsFromAllShifts()
    {
        var rotaId = Guid.NewGuid();
        var shift1 = new Shift { Id = Guid.NewGuid(), RotaId = rotaId };
        var shift2 = new Shift { Id = Guid.NewGuid(), RotaId = rotaId };
        shift1.ShiftSignups.Add(new ShiftSignup { Id = Guid.NewGuid(), ShiftId = shift1.Id });
        shift2.ShiftSignups.Add(new ShiftSignup { Id = Guid.NewGuid(), ShiftId = shift2.Id });
        shift2.ShiftSignups.Add(new ShiftSignup { Id = Guid.NewGuid(), ShiftId = shift2.Id });

        var rota = new Rota { Id = rotaId };
        rota.Shifts.Add(shift1);
        rota.Shifts.Add(shift2);
        rota.Tags.Add(new ShiftTag { Id = Guid.NewGuid(), Name = "indoor" });

        _management.GetRotaForViewAsync(rotaId, Arg.Any<CancellationToken>()).Returns(rota);

        var sut = CreateSut();
        var view = await sut.GetRotaAsync(rotaId);

        view.Rota.Should().BeSameAs(rota);
        view.Shifts.Should().HaveCount(2);
        view.Tags.Should().HaveCount(1);
        view.Signups.Should().HaveCount(3);
    }

    [HumansFact]
    public async Task GetUsersAsync_DeduplicatesAndKeysByUserId()
    {
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        _management.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>())
            .Returns((EventSettings?)null);
        _management.GetVolunteerEventProfileAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((VolunteerEventProfile?)null);
        _signups.GetVolunteerTagPreferencesForUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _signups.GetByUserAsync(Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var sut = CreateSut();
        var batch = await sut.GetUsersAsync([userA, userA, userB]);

        batch.Should().ContainKey(userA);
        batch.Should().ContainKey(userB);
        batch.Should().HaveCount(2);
    }
}
