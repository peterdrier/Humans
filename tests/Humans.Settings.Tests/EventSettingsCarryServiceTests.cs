using AwesomeAssertions;
using Humans.Settings.Contracts;
using Humans.Settings.Services;
using Humans.Shifts.Contracts;
using NodaTime;
using NSubstitute;
using TestContext = Xunit.TestContext;

namespace Humans.Settings.Tests;

/// <summary>
/// The carry is re-runnable, and a rerun has to reconcile Active/Inactive: the
/// Shifts cycle can change between the first carry and PR B's reader cutover, and
/// an existence-only check would leave the old cycle Active forever
/// (nobodies-collective/Humans#1104 review).
/// </summary>
public sealed class EventSettingsCarryServiceTests
{
    private readonly IBurnSettingsService _burn = Substitute.For<IBurnSettingsService>();
    private readonly ISettingsWriteService _settings = Substitute.For<ISettingsWriteService>();

    private EventSettingsCarryService BuildSut() => new(_burn, _settings);

    private static BurnSettingsInfo Burn(Guid id, string name = "Nowhere 2026", int year = 2026) => new(
        Id: id,
        EventName: name,
        Year: year,
        TimeZoneId: "Europe/Madrid",
        GateOpeningDate: new LocalDate(year, 7, 9),
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
        IsShiftBrowsingOpen: false);

    private static EventSettingsInfo Carried(Guid id, EventSettingsStatus status, string name = "Nowhere 2026") => new(
        Id: id,
        EventName: name,
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

    /// <summary>Shifts holds <paramref name="all"/>, with <paramref name="activeId"/> the live cycle.</summary>
    private void ShiftsHas(Guid? activeId, params BurnSettingsInfo[] all)
    {
        _burn.GetAllAsync(Arg.Any<CancellationToken>()).Returns(all);
        _burn.GetActiveAsync(Arg.Any<CancellationToken>())
            .Returns(activeId is null ? null : all.Single(b => b.Id == activeId));
    }

    private void SettingsHas(params EventSettingsInfo[] rows)
    {
        foreach (var row in rows)
            _settings.GetEventSettingsByIdAsync(row.Id, Arg.Any<CancellationToken>()).Returns(row);
    }

    [HumansFact]
    public async Task CarryAsync_InsertsEveryRow_GivingOnlyTheActiveCycleActiveStatus()
    {
        var oldId = Guid.NewGuid();
        var newId = Guid.NewGuid();
        ShiftsHas(newId, Burn(oldId, "Nowhere 2025", 2025), Burn(newId));

        var written = await BuildSut().CarryAsync(TestContext.Current.CancellationToken);

        written.Should().Be(2);
        await _settings.Received(1).SaveEventSettingsAsync(
            Arg.Is<EventSettingsInfo>(s => s.Id == oldId && s.Status == EventSettingsStatus.Inactive),
            Arg.Any<CancellationToken>());
        await _settings.Received(1).SaveEventSettingsAsync(
            Arg.Is<EventSettingsInfo>(s => s.Id == newId && s.Status == EventSettingsStatus.Active),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task CarryAsync_IsANoOpWhenEveryRowIsHereAndInStep()
    {
        var oldId = Guid.NewGuid();
        var newId = Guid.NewGuid();
        ShiftsHas(newId, Burn(oldId, "Nowhere 2025", 2025), Burn(newId));
        SettingsHas(
            Carried(oldId, EventSettingsStatus.Inactive),
            Carried(newId, EventSettingsStatus.Active));

        var written = await BuildSut().CarryAsync(TestContext.Current.CancellationToken);

        written.Should().Be(0);
        await _settings.DidNotReceive().SaveEventSettingsAsync(
            Arg.Any<EventSettingsInfo>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task CarryAsync_ReconcilesStatusWhenTheActiveShiftsCycleChanged()
    {
        // Both rows are already carried, so the old existence check saw nothing to
        // do and left the retired cycle Active.
        var oldId = Guid.NewGuid();
        var newId = Guid.NewGuid();
        ShiftsHas(newId, Burn(oldId, "Nowhere 2025", 2025), Burn(newId));
        SettingsHas(
            Carried(oldId, EventSettingsStatus.Active),
            Carried(newId, EventSettingsStatus.Inactive));

        var written = await BuildSut().CarryAsync(TestContext.Current.CancellationToken);

        written.Should().Be(2);
        Received.InOrder(() =>
        {
            // Deactivate first: the single-active guard refuses a second Active row.
            _ = _settings.SaveEventSettingsAsync(
                Arg.Is<EventSettingsInfo>(s => s.Id == oldId && s.Status == EventSettingsStatus.Inactive),
                Arg.Any<CancellationToken>());
            _ = _settings.SaveEventSettingsAsync(
                Arg.Is<EventSettingsInfo>(s => s.Id == newId && s.Status == EventSettingsStatus.Active),
                Arg.Any<CancellationToken>());
        });
    }

    [HumansFact]
    public async Task CarryAsync_ReconcilesStatusWithoutReCopyingTheOperatorsValues()
    {
        // From the first carry on, the app-wide values are edited on /Settings/Admin.
        // A reconcile must not drag the stale Shifts values back over them.
        var id = Guid.NewGuid();
        ShiftsHas(id, Burn(id, "Stale Shifts name"));
        SettingsHas(Carried(id, EventSettingsStatus.Inactive, "Edited in Settings"));

        await BuildSut().CarryAsync(TestContext.Current.CancellationToken);

        await _settings.Received(1).SaveEventSettingsAsync(
            Arg.Is<EventSettingsInfo>(s =>
                s.Id == id
                && s.Status == EventSettingsStatus.Active
                && s.EventName == "Edited in Settings"),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task GetSnapshotAsync_ReportsStaleStatusAsPendingEvenWhenNothingIsLeftToCarry()
    {
        var oldId = Guid.NewGuid();
        var newId = Guid.NewGuid();
        ShiftsHas(newId, Burn(oldId, "Nowhere 2025", 2025), Burn(newId));
        SettingsHas(
            Carried(oldId, EventSettingsStatus.Active),
            Carried(newId, EventSettingsStatus.Inactive));

        var snapshot = await BuildSut().GetSnapshotAsync(TestContext.Current.CancellationToken);

        snapshot.RemainingCount.Should().Be(0);
        snapshot.StaleStatusCount.Should().Be(2);
        snapshot.PendingCount.Should().Be(2);
        snapshot.Rows.Should().OnlyContain(r => r.AlreadyCarried && r.StatusIsStale);
    }

    [HumansFact]
    public async Task GetSnapshotAsync_ReportsNothingPendingWhenEverythingAgrees()
    {
        var id = Guid.NewGuid();
        ShiftsHas(id, Burn(id));
        SettingsHas(Carried(id, EventSettingsStatus.Active));

        var snapshot = await BuildSut().GetSnapshotAsync(TestContext.Current.CancellationToken);

        snapshot.PendingCount.Should().Be(0);
        snapshot.CarriedCount.Should().Be(1);
        snapshot.Rows.Should().OnlyContain(r => r.IsActive && !r.StatusIsStale);
    }

    [HumansFact]
    public async Task GetSnapshotAsync_CountsAnUncarriedRowAsRemainingNotStale()
    {
        var id = Guid.NewGuid();
        ShiftsHas(id, Burn(id));

        var snapshot = await BuildSut().GetSnapshotAsync(TestContext.Current.CancellationToken);

        snapshot.RemainingCount.Should().Be(1);
        snapshot.StaleStatusCount.Should().Be(0);
        snapshot.PendingCount.Should().Be(1);
    }

    [HumansFact]
    public async Task CarryAsync_WithNoActiveShiftsCycle_WritesEverythingInactive()
    {
        var id = Guid.NewGuid();
        ShiftsHas(activeId: null, Burn(id));

        await BuildSut().CarryAsync(TestContext.Current.CancellationToken);

        await _settings.Received(1).SaveEventSettingsAsync(
            Arg.Is<EventSettingsInfo>(s => s.Status == EventSettingsStatus.Inactive),
            Arg.Any<CancellationToken>());
    }
}
