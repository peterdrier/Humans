using AwesomeAssertions;
using Humans.Settings.Contracts;
using Humans.Settings.Data;
using Humans.Settings.Domain;
using Humans.Settings.Services;
using Humans.Shifts.Contracts;
using NodaTime;
using NSubstitute;
using TestContext = Xunit.TestContext;

namespace Humans.Settings.Tests;

/// <summary>
/// The section boundary: <see cref="Service"/> maps the internal
/// <see cref="EventSettings"/> entity to <see cref="EventSettingsInfo"/> on the
/// way out and back on the way in. Backfills the coverage that lived in Shifts'
/// deleted <c>BurnSettingsServiceTests</c> before the app-wide values moved here
/// (nobodies-collective/Humans#1104).
/// </summary>
public sealed class ServiceTests
{
    private static readonly Instant Now = Instant.FromUtc(2026, 8, 21, 10, 0);

    private readonly ISettingsRepository _repository = Substitute.For<ISettingsRepository>();
    private readonly IBurnSettingsService _burnSettings = Substitute.For<IBurnSettingsService>();
    private readonly IClock _clock = Substitute.For<IClock>();

    public ServiceTests() => _clock.GetCurrentInstant().Returns(Now);

    private Service BuildSut() => new(_repository, _burnSettings, _clock);

    /// <summary>Makes <paramref name="id"/> an id Shifts' event_settings knows.</summary>
    private void ShiftsKnows(Guid id) =>
        _burnSettings.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(new BurnSettingsInfo(
            Id: id,
            EventName: "Nowhere 2026",
            Year: 2026,
            TimeZoneId: "Europe/Madrid",
            GateOpeningDate: new LocalDate(2026, 7, 9),
            BuildStartOffset: -25,
            EventEndOffset: 6,
            StrikeEndOffset: 9,
            FirstCrewStartOffset: -25,
            SetupWeekStartOffset: -16,
            PreEventWeekStartOffset: -9,
            FinishingWeekendStartOffset: -4,
            EarlyEntryCapacity: new Dictionary<int, int>(),
            BarriosEarlyEntryAllocation: null,
            EarlyEntryClose: null,
            IsShiftBrowsingOpen: false));

    private static EventSettings MakeEntity(Guid id, bool isActive = true) => new()
    {
        Id = id,
        EventName = "Nowhere 2026",
        Year = 2026,
        TimeZoneId = "Europe/Madrid",
        GateOpeningDate = new LocalDate(2026, 7, 9),
        BuildStartOffset = -25,
        EventEndOffset = 6,
        StrikeEndOffset = 9,
        FirstCrewStartOffset = -25,
        SetupWeekStartOffset = -16,
        PreEventWeekStartOffset = -9,
        FinishingWeekendStartOffset = -4,
        EarlyEntryCapacity = new Dictionary<int, int> { [-14] = 50, [-7] = 100 },
        BarriosEarlyEntryAllocation = new Dictionary<int, int> { [-7] = 20 },
        EarlyEntryClose = Instant.FromUtc(2026, 7, 4, 12, 0),
        Status = isActive ? EventSettingsStatus.Active : EventSettingsStatus.Inactive,
    };

    [HumansFact]
    public async Task GetActiveEventSettingsAsync_MapsEveryFieldOntoTheDto()
    {
        var id = Guid.NewGuid();
        _repository.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>()).Returns(MakeEntity(id));

        var dto = await BuildSut().GetActiveEventSettingsAsync(TestContext.Current.CancellationToken);

        dto.Should().NotBeNull();
        dto!.Id.Should().Be(id);
        dto.EventName.Should().Be("Nowhere 2026");
        dto.Year.Should().Be(2026);
        dto.TimeZoneId.Should().Be("Europe/Madrid");
        dto.GateOpeningDate.Should().Be(new LocalDate(2026, 7, 9));
        dto.BuildStartOffset.Should().Be(-25);
        dto.EventEndOffset.Should().Be(6);
        dto.StrikeEndOffset.Should().Be(9);
        dto.FirstCrewStartOffset.Should().Be(-25);
        dto.SetupWeekStartOffset.Should().Be(-16);
        dto.PreEventWeekStartOffset.Should().Be(-9);
        dto.FinishingWeekendStartOffset.Should().Be(-4);
        dto.EarlyEntryCapacity.Should().Equal(new Dictionary<int, int> { [-14] = 50, [-7] = 100 });
        dto.BarriosEarlyEntryAllocation.Should().Equal(new Dictionary<int, int> { [-7] = 20 });
        dto.EarlyEntryClose.Should().Be(Instant.FromUtc(2026, 7, 4, 12, 0));
        dto.Status.Should().Be(EventSettingsStatus.Active);
    }

    [HumansFact]
    public async Task GetActiveEventSettingsAsync_CopiesTheDictionaries()
    {
        // The entity is the section's own mutable EF object; handing its
        // dictionary out by reference would let a caller edit tracked state.
        var entity = MakeEntity(Guid.NewGuid());
        _repository.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>()).Returns(entity);

        var dto = await BuildSut().GetActiveEventSettingsAsync(TestContext.Current.CancellationToken);

        dto!.EarlyEntryCapacity.Should().NotBeSameAs(entity.EarlyEntryCapacity);
        dto.BarriosEarlyEntryAllocation.Should().NotBeSameAs(entity.BarriosEarlyEntryAllocation);
    }

    [HumansFact]
    public async Task GetActiveEventSettingsAsync_ReturnsNullWhenNoEventIsActive()
    {
        _repository.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>()).Returns((EventSettings?)null);

        var dto = await BuildSut().GetActiveEventSettingsAsync(TestContext.Current.CancellationToken);

        dto.Should().BeNull();
    }

    [HumansFact]
    public async Task GetEventSettingsByIdAsync_ReturnsNullForAnUnknownId()
    {
        var unknown = Guid.NewGuid();
        _repository.GetEventSettingsByIdAsync(unknown, Arg.Any<CancellationToken>())
            .Returns((EventSettings?)null);

        var dto = await BuildSut().GetEventSettingsByIdAsync(unknown, TestContext.Current.CancellationToken);

        dto.Should().BeNull();
    }

    [HumansFact]
    public async Task GetEventSettingsByIdAsync_MapsAnInactiveRow()
    {
        // Historical cycles are read by id and are not active — Rota.EventSettingsId
        // points at them long after the event.
        var id = Guid.NewGuid();
        _repository.GetEventSettingsByIdAsync(id, Arg.Any<CancellationToken>())
            .Returns(MakeEntity(id, isActive: false));

        var dto = await BuildSut().GetEventSettingsByIdAsync(id, TestContext.Current.CancellationToken);

        dto!.Id.Should().Be(id);
        dto.Status.Should().Be(EventSettingsStatus.Inactive);
    }

    [HumansFact]
    public async Task GetActiveEventSettingsAsync_MapsANullBarriosAllocation()
    {
        var entity = MakeEntity(Guid.NewGuid());
        entity.BarriosEarlyEntryAllocation = null;
        _repository.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>()).Returns(entity);

        var dto = await BuildSut().GetActiveEventSettingsAsync(TestContext.Current.CancellationToken);

        dto!.BarriosEarlyEntryAllocation.Should().BeNull();
    }

    [HumansFact]
    public async Task SaveEventSettingsAsync_RoundTripsTheDtoBackOntoTheEntityAndStampsTheClock()
    {
        var id = Guid.NewGuid();
        ShiftsKnows(id);
        var dto = new EventSettingsInfo(
            Id: id,
            EventName: "Nowhere 2027",
            Year: 2027,
            TimeZoneId: "Atlantic/Canary",
            GateOpeningDate: new LocalDate(2027, 7, 8),
            BuildStartOffset: -20,
            EventEndOffset: 5,
            StrikeEndOffset: 8,
            FirstCrewStartOffset: -20,
            SetupWeekStartOffset: -14,
            PreEventWeekStartOffset: -8,
            FinishingWeekendStartOffset: -3,
            EarlyEntryCapacity: new Dictionary<int, int> { [-12] = 40 },
            BarriosEarlyEntryAllocation: null,
            EarlyEntryClose: null,
            Status: EventSettingsStatus.Inactive);

        await BuildSut().SaveEventSettingsAsync(dto, TestContext.Current.CancellationToken);

        await _repository.Received(1).UpsertEventSettingsAsync(
            Arg.Is<EventSettings>(e =>
                e.Id == id
                && e.EventName == "Nowhere 2027"
                && e.Year == 2027
                && e.TimeZoneId == "Atlantic/Canary"
                && e.GateOpeningDate == new LocalDate(2027, 7, 8)
                && e.BuildStartOffset == -20
                && e.EventEndOffset == 5
                && e.StrikeEndOffset == 8
                && e.FirstCrewStartOffset == -20
                && e.SetupWeekStartOffset == -14
                && e.PreEventWeekStartOffset == -8
                && e.FinishingWeekendStartOffset == -3
                && e.EarlyEntryCapacity[-12] == 40
                && e.BarriosEarlyEntryAllocation == null
                && e.EarlyEntryClose == null
                && e.Status == EventSettingsStatus.Inactive),
            Now,
            Arg.Any<CancellationToken>());
    }

    // ── The at-most-one-Active invariant (nobodies-collective/Humans#1104 review).
    //    No DB constraint backs it, so the service is where it holds.

    private static EventSettingsInfo MakeDto(Guid id, EventSettingsStatus status) => new(
        Id: id,
        EventName: "Nowhere 2026",
        Year: 2026,
        TimeZoneId: "Europe/Madrid",
        GateOpeningDate: new LocalDate(2026, 7, 9),
        BuildStartOffset: -25,
        EventEndOffset: 6,
        StrikeEndOffset: 9,
        FirstCrewStartOffset: -25,
        SetupWeekStartOffset: -16,
        PreEventWeekStartOffset: -9,
        FinishingWeekendStartOffset: -4,
        EarlyEntryCapacity: new Dictionary<int, int>(),
        BarriosEarlyEntryAllocation: null,
        EarlyEntryClose: null,
        Status: status);

    [HumansFact]
    public async Task SaveEventSettingsAsync_RefusesToActivateWhileAnotherRowIsActive()
    {
        var id = Guid.NewGuid();
        ShiftsKnows(id);
        _repository.AnyOtherActiveEventSettingsAsync(id, Arg.Any<CancellationToken>()).Returns(true);

        var act = () => BuildSut().SaveEventSettingsAsync(
            MakeDto(id, EventSettingsStatus.Active), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Only one event settings row can be Active*");
        await _repository.DidNotReceive().UpsertEventSettingsAsync(
            Arg.Any<EventSettings>(), Arg.Any<Instant>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SaveEventSettingsAsync_LetsTheAlreadyActiveRowSaveAgain()
    {
        // The guard excludes the row being saved, so re-saving the active row is
        // an ordinary edit — only a *second* active row is refused.
        var id = Guid.NewGuid();
        ShiftsKnows(id);
        _repository.AnyOtherActiveEventSettingsAsync(id, Arg.Any<CancellationToken>()).Returns(false);

        await BuildSut().SaveEventSettingsAsync(
            MakeDto(id, EventSettingsStatus.Active), TestContext.Current.CancellationToken);

        await _repository.Received(1).UpsertEventSettingsAsync(
            Arg.Is<EventSettings>(e => e.Status == EventSettingsStatus.Active),
            Now, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SaveEventSettingsAsync_DoesNotCheckActivationWhenSavingAnInactiveRow()
    {
        // Leaving zero rows active is allowed — deactivating is how a cycle ends.
        var id = Guid.NewGuid();
        ShiftsKnows(id);

        await BuildSut().SaveEventSettingsAsync(
            MakeDto(id, EventSettingsStatus.Inactive), TestContext.Current.CancellationToken);

        await _repository.DidNotReceive().AnyOtherActiveEventSettingsAsync(
            Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _repository.Received(1).UpsertEventSettingsAsync(
            Arg.Any<EventSettings>(), Now, Arg.Any<CancellationToken>());
    }

    // ── The id-coordination invariant: Rota.EventSettingsId still resolves against
    //    Shifts' event_settings, so a row born here needs an id Shifts already has.

    [HumansFact]
    public async Task SaveEventSettingsAsync_RefusesANewRowWhoseIdNamesNoShiftsEvent()
    {
        var id = Guid.NewGuid();
        _burnSettings.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns((BurnSettingsInfo?)null);

        var act = () => BuildSut().SaveEventSettingsAsync(
            MakeDto(id, EventSettingsStatus.Inactive), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"No Shifts event row has id {id}*");
        await _repository.DidNotReceive().UpsertEventSettingsAsync(
            Arg.Any<EventSettings>(), Arg.Any<Instant>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SaveEventSettingsAsync_UpdatesAnExistingRowWithoutAskingShifts()
    {
        // Only inserts need the id check; a row already here was vetted on the way in.
        var id = Guid.NewGuid();
        _repository.GetEventSettingsByIdAsync(id, Arg.Any<CancellationToken>())
            .Returns(MakeEntity(id, isActive: false));

        await BuildSut().SaveEventSettingsAsync(
            MakeDto(id, EventSettingsStatus.Inactive), TestContext.Current.CancellationToken);

        await _burnSettings.DidNotReceive().GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _repository.Received(1).UpsertEventSettingsAsync(
            Arg.Any<EventSettings>(), Now, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task GetValueAsync_AndSetValueAsync_PassThroughToTheRepository()
    {
        _repository.GetValueAsync("Feature:Enabled", Arg.Any<CancellationToken>()).Returns("true");
        var sut = BuildSut();

        (await sut.GetValueAsync("Feature:Enabled", TestContext.Current.CancellationToken))
            .Should().Be("true");

        await sut.SetValueAsync("Feature:Enabled", "false", TestContext.Current.CancellationToken);

        await _repository.Received(1).SetValueAsync(
            "Feature:Enabled", "false", Arg.Any<CancellationToken>());
    }
}
