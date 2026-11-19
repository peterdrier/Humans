using Humans.Shifts.Data;
using Humans.Shifts.Domain;
using AwesomeAssertions;
using Humans.Settings.Contracts;
using Humans.Shifts.Services;
using Humans.Shifts.Services.Dtos;
using Humans.Shifts.Contracts;
using NSubstitute;

namespace Humans.Shifts.Tests.Services;

public class ShiftViewServiceTests
{
    private readonly IShiftManagementRepository _management = Substitute.For<IShiftManagementRepository>();
    private readonly IVolunteerTrackingRepository _tracking = Substitute.For<IVolunteerTrackingRepository>();
    private readonly ISettingsService _settingsService = Substitute.For<ISettingsService>();
    private readonly EventCalendarResolver _calendarResolver;
    private readonly IShiftManagement _shiftManagement = Substitute.For<IShiftManagement>();
    private readonly IShiftRotaRows _rotaRows = Substitute.For<IShiftRotaRows>();

    public ShiftViewServiceTests()
    {
        _calendarResolver = new(_settingsService, Substitute.For<Humans.Shifts.Data.IShiftManagementRepository>());
        _rotaRows.GetRotaRowsAsync(Arg.Any<IReadOnlyCollection<Guid>?>(), Arg.Any<CancellationToken>()).Returns([]);
    }

    private ShiftViewService CreateSut() =>
        new(_management, _tracking, _calendarResolver, _shiftManagement, _rotaRows);

    [HumansFact]
    public void EventSettingsChanged_EvictsThatEventsDashboardCaches()
    {
        var eventSettingsId = Guid.NewGuid();

        CreateSut().EventSettingsChanged(eventSettingsId);

        _shiftManagement.Received(1).InvalidateDashboardCaches(eventSettingsId);
    }

    private void StubActiveCalendar(EventSettingsInfo? calendar) =>
        _settingsService.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>()).Returns(calendar);

    private static EventSettingsInfo Calendar(Guid id) => new(
        Id: id, EventName: "Nowhere 2026", Year: 2026, TimeZoneId: "Europe/Madrid",
        GateOpeningDate: new NodaTime.LocalDate(2026, 7, 1), BuildStartOffset: -14, EventEndOffset: 6,
        StrikeEndOffset: 9,
        EarlyEntryCapacity: new Dictionary<int, int>(), BarriosEarlyEntryAllocation: null,
        EarlyEntryClose: null);

    [HumansFact]
    public async Task GetUserAsync_NoActiveEvent_ReturnsEmptyAvailabilityAndBuildStatus()
    {
        var userId = Guid.NewGuid();
        StubActiveCalendar(null);
        _management.GetVolunteerEventProfilesByUserIdsAsync(
                Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(userId)),
                Arg.Any<CancellationToken>())
            .Returns([]);
        _management.GetVolunteerTagPreferencesForUsersAsync(
                Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(userId)),
                Arg.Any<CancellationToken>())
            .Returns([]);

        var sut = CreateSut();
        var view = await sut.GetUserAsync(userId, Xunit.TestContext.Current.CancellationToken);

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
        var profile = new VolunteerEventProfile { UserId = userId };
        var availability = new GeneralAvailability { UserId = userId, EventSettingsId = eventId };
        var buildStatus = new VolunteerBuildStatus { UserId = userId, EventSettingsId = eventId };

        StubActiveCalendar(Calendar(eventId));
        _management.GetVolunteerEventProfilesByUserIdsAsync(
                Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(userId)),
                Arg.Any<CancellationToken>())
            .Returns([profile]);
        _management.GetVolunteerTagPreferencesForUsersAsync(
                Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(userId)),
                Arg.Any<CancellationToken>())
            .Returns([]);
        _tracking.GetAvailabilityForEventAsync(
                eventId,
                Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(userId)),
                Arg.Any<CancellationToken>())
            .Returns([availability]);
        _tracking.GetBuildStatusesForEventAsync(
                eventId,
                Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(userId)),
                Arg.Any<CancellationToken>())
            .Returns([buildStatus]);

        var sut = CreateSut();
        var view = await sut.GetUserAsync(userId, Xunit.TestContext.Current.CancellationToken);

        view.Profile.Should().BeSameAs(profile);
        view.Calendar!.Id.Should().Be(eventId);
        view.Availability.Should().BeSameAs(availability);
        view.BuildStatus.Should().BeSameAs(buildStatus);
    }

    [HumansFact]
    public async Task GetUserAsync_NoActiveEvent_DoesNotQuerySignups()
    {
        var userId = Guid.NewGuid();
        StubActiveCalendar(null);
        _management.GetVolunteerEventProfilesByUserIdsAsync(
                Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(userId)),
                Arg.Any<CancellationToken>())
            .Returns([]);
        _management.GetVolunteerTagPreferencesForUsersAsync(
                Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(userId)),
                Arg.Any<CancellationToken>())
            .Returns([]);

        var sut = CreateSut();
        var view = await sut.GetUserAsync(userId, Xunit.TestContext.Current.CancellationToken);

        view.Signups.Should().BeEmpty();
        await _rotaRows.DidNotReceive().GetRotaRowsAsync(
            Arg.Any<IReadOnlyCollection<Guid>?>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task GetUserAsync_WithActiveEvent_TakesOnlyThatEventsSignupsFromTheRotaRows()
    {
        var userId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var mine = new ShiftAdminSignup(Guid.NewGuid(), userId, null, SignupStatus.Confirmed, default);
        var otherEvent = mine with { Id = Guid.NewGuid() };
        var otherUser = mine with { Id = Guid.NewGuid(), UserId = Guid.NewGuid() };
        _rotaRows.GetRotaRowsAsync(null, Arg.Any<CancellationToken>()).Returns(
        [
            Row(eventId, mine, otherUser),
            Row(Guid.NewGuid(), otherEvent),
        ]);

        StubActiveCalendar(Calendar(eventId));
        _management.GetVolunteerEventProfilesByUserIdsAsync(
                Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(userId)),
                Arg.Any<CancellationToken>())
            .Returns([]);
        _management.GetVolunteerTagPreferencesForUsersAsync(
                Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(userId)),
                Arg.Any<CancellationToken>())
            .Returns([]);

        var view = await CreateSut().GetUserAsync(userId, Xunit.TestContext.Current.CancellationToken);

        view.Signups.Should().ContainSingle().Which.Signup.Should().BeSameAs(mine);
    }

    private static ShiftAdminRota Row(Guid eventId, params ShiftAdminSignup[] signups)
    {
        var rota = new RotaInfo(Guid.NewGuid(), eventId, Guid.NewGuid(), "Gate", null, null,
            ShiftPriority.Normal, SignupPolicy.Public, RotaPeriod.Event, true, []);
        var shift = new ShiftInfo(Guid.NewGuid(), rota.Id, null, 0, new NodaTime.LocalTime(10, 0), 4, 1, 2,
            false, false, false);
        return new ShiftAdminRota(rota, [new ShiftAdminShift(shift, signups)]);
    }

    [HumansFact]
    public async Task GetRotaAsync_UnknownRotaId_ReturnsEmptyView()
    {
        var rotaId = Guid.NewGuid();
        _management.GetRotaAsync(rotaId, RotaReadShape.View, Arg.Any<CancellationToken>())
            .Returns((Rota?)null);

        var sut = CreateSut();
        var view = await sut.GetRotaAsync(rotaId, Xunit.TestContext.Current.CancellationToken);

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

        _management.GetRotaAsync(rotaId, RotaReadShape.View, Arg.Any<CancellationToken>()).Returns(rota);

        var sut = CreateSut();
        var view = await sut.GetRotaAsync(rotaId, Xunit.TestContext.Current.CancellationToken);

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
        StubActiveCalendar(null);
        _management.GetVolunteerEventProfileAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((VolunteerEventProfile?)null);
        _management.GetVolunteerTagPreferencesForUsersAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(),
                Arg.Any<CancellationToken>())
            .Returns([]);

        var sut = CreateSut();
        var batch = await sut.GetUsersAsync([userA, userA, userB], Xunit.TestContext.Current.CancellationToken);

        batch.Should().ContainKey(userA);
        batch.Should().ContainKey(userB);
        batch.Should().HaveCount(2);
    }
}
