using AwesomeAssertions;
using Humans.Base.Constants;
using Humans.Base.Enums;
using Humans.Settings.Contracts;
using Humans.Shifts.Contracts;
using Humans.Shifts.Services;
using Humans.Shifts.Services.Dtos;
using Humans.Teams.Contracts;
using NodaTime;
using NSubstitute;
using Xunit;

namespace Humans.Shifts.Tests.Services;

public class ShiftGoogleGroupSourceTests
{
    private static readonly string Domain = DomainConstants.GoogleGroupDomain;
    private readonly IShiftRotaRows _rotaRows = Substitute.For<IShiftRotaRows>();
    private readonly ITeamService _teams = Substitute.For<ITeamService>();
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly Guid _eventId = Guid.NewGuid();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ShiftGoogleGroupSource Source() => new(_rotaRows, _teams, _settings);

    [HumansFact]
    public async Task GetExpectedAsync_ConfirmedSignups_FillDepartmentList()
    {
        var cantina = Team("cantina", prefix: "cantina");
        var kitchen = Team("kitchen", parent: cantina);
        var (confirmed, subTeamConfirmed, pending, bailed) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        Arrange([cantina, kitchen],
            Row(_eventId, cantina, Signup(confirmed, SignupStatus.Confirmed), Signup(pending, SignupStatus.Pending)),
            Row(_eventId, kitchen, Signup(subTeamConfirmed, SignupStatus.Confirmed), Signup(bailed, SignupStatus.Bailed)),
            Row(_eventId, kitchen, Signup(confirmed, SignupStatus.Confirmed)));

        var lists = await Source().GetExpectedAsync(ct: Ct);

        lists.Keys.Should().Equal($"cantina-2026-shifts@{Domain}");
        lists.Values.Single().Should().BeEquivalentTo([confirmed, subTeamConfirmed]);
    }

    [HumansFact]
    public async Task GetExpectedAsync_NoGroupPrefix_UsesSlug()
    {
        var gate = Team("gate-crew");
        Arrange([gate], Row(_eventId, gate, Signup(Guid.NewGuid(), SignupStatus.Confirmed)));

        var lists = await Source().GetExpectedAsync($"GATE-CREW-2026-SHIFTS@{Domain}", Ct);

        lists.Keys.Should().Equal($"gate-crew-2026-shifts@{Domain}");
    }

    [HumansFact]
    public async Task GetExpectedAsync_OtherEventSignups_ClaimNothing()
    {
        var gate = Team("gate");
        Arrange([gate], Row(Guid.NewGuid(), gate, Signup(Guid.NewGuid(), SignupStatus.Confirmed)));

        (await Source().GetExpectedAsync(ct: Ct)).Should().BeEmpty();
    }

    [HumansFact]
    public async Task GetExpectedAsync_NoActiveEvent_ClaimsNothing()
    {
        var gate = Team("gate");
        Arrange([gate], Row(_eventId, gate, Signup(Guid.NewGuid(), SignupStatus.Confirmed)));
        _settings.GetActiveEventSettings().Returns((EventSettingsInfo?)null);

        (await Source().GetExpectedAsync(ct: Ct)).Should().BeEmpty();
    }

    [HumansFact]
    public async Task GetExpectedAsync_TwoDepartmentsOneAddress_ClaimsNeither()
    {
        var bar = Team("bar");
        var drinks = Team("drinks", prefix: "bar");
        Arrange([bar, drinks],
            Row(_eventId, bar, Signup(Guid.NewGuid(), SignupStatus.Confirmed)),
            Row(_eventId, drinks, Signup(Guid.NewGuid(), SignupStatus.Confirmed)));

        (await Source().GetExpectedAsync($"bar-2026-shifts@{Domain}", Ct)).Should().BeEmpty();
    }

    private void Arrange(TeamInfo[] teams, params ShiftAdminRota[] rows)
    {
        _settings.GetActiveEventSettings().Returns(new EventSettingsInfo(
            Id: _eventId, EventName: "Nowhere 2026", Year: 2026, TimeZoneId: "Europe/Madrid",
            GateOpeningDate: new LocalDate(2026, 7, 1), BuildStartOffset: -14, EventEndOffset: 6, StrikeEndOffset: 9,
            EarlyEntryCapacity: new Dictionary<int, int>(), BarriosEarlyEntryAllocation: null, EarlyEntryClose: null));
        _teams.GetTeamsAsync(Arg.Any<CancellationToken>()).Returns(teams.ToDictionary(t => t.Id));
        _rotaRows.GetRotaRowsAsync(Arg.Any<IReadOnlyCollection<Guid>?>(), Arg.Any<CancellationToken>()).Returns(rows);
    }

    private static TeamInfo Team(string slug, string? prefix = null, TeamInfo? parent = null) => new(
        Guid.NewGuid(), slug, null, slug, true, false, SystemTeamType.None, false, false, false, false,
        Instant.FromUtc(2026, 1, 1, 0, 0), [], ParentTeamId: parent?.Id, GoogleGroupPrefix: prefix);

    private static ShiftAdminSignup Signup(Guid userId, SignupStatus status) =>
        new(Guid.NewGuid(), userId, null, status, Instant.FromUtc(2026, 6, 1, 0, 0));

    private static ShiftAdminRota Row(Guid eventId, TeamInfo team, params ShiftAdminSignup[] signups)
    {
        var rota = new RotaInfo(Guid.NewGuid(), eventId, team.Id, "Rota", null, null,
            ShiftPriority.Normal, SignupPolicy.Public, RotaPeriod.Event, true, []);
        var shift = new ShiftInfo(Guid.NewGuid(), rota.Id, null, 0, new LocalTime(10, 0), 4, 1, 2, false, false, false);
        return new ShiftAdminRota(rota, [new ShiftAdminShift(shift, signups)]);
    }
}
