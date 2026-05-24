using AwesomeAssertions;
using Humans.Application;
using Humans.Application.DTOs.VolunteerTrackingExport;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Shifts;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;
using NSubstitute;

namespace Humans.Application.Tests.Services.Shifts;

public sealed class VolunteerTrackingExportServiceTests
{
    // Fixed test event: Elsewhere 2026 in Europe/Madrid, with stable IDs for assertions.
    private static readonly Guid EventId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid TeamA = Guid.Parse("11111111-0000-0000-0000-000000000000");
    private static readonly Guid TeamB = Guid.Parse("22222222-0000-0000-0000-000000000000");

    private static readonly Instant TestNow = Instant.FromUtc(2026, 5, 23, 12, 0);
    private static readonly LocalDate Day1 = new(2026, 7, 7);
    private static readonly LocalDate Day7 = new(2026, 7, 13);

    private static VolunteerExportRequest BuildRequest(
        Guid? departmentId = null,
        LocalDate? start = null,
        LocalDate? end = null,
        ShiftPeriod? period = null) =>
        new(
            EventSettingsId: EventId,
            DepartmentId: departmentId,
            StartDate: start ?? Day1,
            EndDate: end ?? Day7,
            Period: period,
            ActorPlayaName: "TestActor",
            GeneratedAtUtc: TestNow);

    private static (IVolunteerTrackingRepository repo, IShiftManagementService shiftMgmt, IUserService users)
        BuildMocks(
            IReadOnlyList<ConfirmedShiftRow> shifts,
            IReadOnlyList<(Guid TeamId, string TeamName)>? departments = null,
            IReadOnlyDictionary<Guid, string>? playaNames = null)
    {
        var repo = Substitute.For<IVolunteerTrackingRepository>();
        repo.GetConfirmedShiftsInRangeAsync(
            Arg.Any<Guid>(), Arg.Any<LocalDate>(), Arg.Any<LocalDate>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(shifts);

        var shiftMgmt = Substitute.For<IShiftManagementService>();
        shiftMgmt.GetDepartmentsWithRotasAsync(EventId)
            .Returns(departments ?? Array.Empty<(Guid, string)>());
        shiftMgmt.GetByIdAsync(EventId)
            .Returns(new EventSettings
            {
                Id = EventId,
                Year = 2026,
                TimeZoneId = "Europe/Madrid",
                GateOpeningDate = Day1,
            });

        var users = Substitute.For<IUserService>();
        if (playaNames is not null)
        {
            foreach (var (userId, name) in playaNames)
            {
                var userInfo = MakeUserInfo(userId, name);
                users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(userInfo);
            }
        }

        return (repo, shiftMgmt, users);
    }

    private static UserInfo MakeUserInfo(Guid userId, string burnerName) =>
        UserInfo.Create(
            user: new User
            {
                Id = userId,
                DisplayName = burnerName,
                PreferredLanguage = "en",
                CreatedAt = TestNow,
                GoogleEmailStatus = GoogleEmailStatus.Unknown,
            },
            userEmails: [],
            eventParticipations: [],
            externalLogins: [],
            profile: null,
            contactFields: [],
            profileLanguages: [],
            volunteerHistory: [],
            communicationPreferences: []);

    [HumansFact]
    public async Task EmptyRange_ReturnsModelWithNoGroupsButFullMetadata()
    {
        var (repo, shiftMgmt, users) = BuildMocks(shifts: Array.Empty<ConfirmedShiftRow>());
        var sut = new VolunteerTrackingExportService(repo, shiftMgmt, users);

        var model = await sut.BuildAsync(BuildRequest(), ct: default);

        model.Groups.Should().BeEmpty();
        model.TotalsPerDay.Should().AllBeEquivalentTo(0);
        model.Days.Should().HaveCount(7);
        model.MethodologyBlurb.Should().NotBeNullOrWhiteSpace();
        model.FilterSummary.Should().NotBeNullOrWhiteSpace();
        model.GeneratedByName.Should().Be("TestActor");
        model.SuggestedFileName.Should().Be("volunteer-tracking-2026-07-07-to-2026-07-13.xlsx");
    }

    private static readonly Guid Alice = Guid.Parse("a0000000-0000-0000-0000-000000000001");

    [HumansFact]
    public async Task SingleHuman_ThreeConsecutiveShifts_SingleTeam()
    {
        // Alice has confirmed TeamA shifts on Day3, Day4, Day5 (in event-local).
        var shifts = new[]
        {
            ShiftRow(Alice, TeamA, Day1.PlusDays(2), 9, 17),
            ShiftRow(Alice, TeamA, Day1.PlusDays(3), 9, 17),
            ShiftRow(Alice, TeamA, Day1.PlusDays(4), 9, 17),
        };
        var (repo, shiftMgmt, users) = BuildMocks(
            shifts: shifts,
            departments: [(TeamA, "TeamA")],
            playaNames: new Dictionary<Guid, string> { [Alice] = "Alice" });
        var sut = new VolunteerTrackingExportService(repo, shiftMgmt, users);

        var model = await sut.BuildAsync(BuildRequest(), ct: default);

        model.Groups.Should().HaveCount(1);
        var group = model.Groups[0];
        group.TeamId.Should().Be(TeamA);
        group.TeamName.Should().Be("TeamA");
        group.Humans.Should().HaveCount(1);

        var row = group.Humans[0];
        row.PlayaName.Should().Be("Alice");
        row.Cells.Should().HaveCount(7);
        // Day 0 = before arrival (Alice's first shift is Day3 → arrival = Day2).
        row.Cells[0].Kind.Should().Be(CellKind.Empty);
        // Day 1 (index 1 = Day2) is one day before her first shift → arrival = white.
        row.Cells[1].Kind.Should().Be(CellKind.Arrival);
        // Day 2 (index 2 = Day3) — first shift — worked TeamA.
        row.Cells[2].Kind.Should().Be(CellKind.Worked);
        row.Cells[2].TeamId.Should().Be(TeamA);
        row.Cells[3].Kind.Should().Be(CellKind.Worked);
        row.Cells[4].Kind.Should().Be(CellKind.Worked);
        row.Cells[5].Kind.Should().Be(CellKind.Empty); // no shift Day6
        row.Cells[6].Kind.Should().Be(CellKind.Empty); // no shift Day7

        // Totals: 1 on Day3-5, 0 elsewhere (presence = has shift that day per spec).
        model.TotalsPerDay.Should().Equal(0, 0, 1, 1, 1, 0, 0);
    }

    [HumansFact]
    public async Task ShiftSpanningTwoDays_AppearsOnBothDays()
    {
        // Alice has a TeamA shift starting 22:00 Day3 (local), ending 06:00 Day4 (local).
        // 2h on Day3, 6h on Day4.
        var zone = DateTimeZoneProviders.Tzdb["Europe/Madrid"];
        var startInstant = (Day1.PlusDays(2) + LocalTime.FromHourMinuteSecondTick(22, 0, 0, 0))
            .InZoneStrictly(zone).ToInstant();
        var endInstant = (Day1.PlusDays(3) + LocalTime.FromHourMinuteSecondTick(6, 0, 0, 0))
            .InZoneStrictly(zone).ToInstant();
        var shifts = new[] { new ConfirmedShiftRow(Alice, TeamA, startInstant, endInstant) };
        var (repo, shiftMgmt, users) = BuildMocks(
            shifts: shifts,
            departments: [(TeamA, "TeamA")],
            playaNames: new Dictionary<Guid, string> { [Alice] = "Alice" });
        var sut = new VolunteerTrackingExportService(repo, shiftMgmt, users);

        var model = await sut.BuildAsync(BuildRequest(), ct: default);

        var cells = model.Groups[0].Humans[0].Cells;
        cells[2].Kind.Should().Be(CellKind.Worked); // Day3
        cells[3].Kind.Should().Be(CellKind.Worked); // Day4
    }

    private static readonly Guid Bob = Guid.Parse("b0000000-0000-0000-0000-000000000002");

    [HumansFact]
    public async Task DepartmentFilter_OnlyShowsThatDeptsWork()
    {
        // Bob worked TeamA on Day3, TeamB on Day4. Filtered to TeamA: row appears,
        // Day3 colored, Day4 empty, arrival = Day2 (day before TeamA's first shift).
        var teamAOnly = new[] { ShiftRow(Bob, TeamA, Day1.PlusDays(2), 9, 17) };
        var repo = Substitute.For<IVolunteerTrackingRepository>();
        repo.GetConfirmedShiftsInRangeAsync(EventId, Day1, Day7, TeamA, Arg.Any<CancellationToken>())
            .Returns(teamAOnly);
        var shiftMgmt = Substitute.For<IShiftManagementService>();
        shiftMgmt.GetDepartmentsWithRotasAsync(EventId).Returns([(TeamA, "TeamA")]);
        shiftMgmt.GetByIdAsync(EventId).Returns(new EventSettings
        {
            Id = EventId,
            Year = 2026,
            TimeZoneId = "Europe/Madrid",
            GateOpeningDate = Day1,
        });
        var users = Substitute.For<IUserService>();
        users.GetUserInfoAsync(Bob, Arg.Any<CancellationToken>()).Returns(MakeUserInfo(Bob, "Bob"));

        var sut = new VolunteerTrackingExportService(repo, shiftMgmt, users);
        var model = await sut.BuildAsync(BuildRequest(departmentId: TeamA), ct: default);

        model.Groups.Should().HaveCount(1);
        var cells = model.Groups[0].Humans[0].Cells;
        cells[1].Kind.Should().Be(CellKind.Arrival);          // Day2
        cells[2].Kind.Should().Be(CellKind.Worked);           // Day3 — TeamA
        cells[3].Kind.Should().Be(CellKind.Empty);            // Day4 — TeamB excluded
    }

    [HumansFact]
    public async Task ArrivalDayOutsideRange_NoWhiteCell_FirstInRangeCellColorsNormally()
    {
        // Alice's first confirmed shift is exactly Day1 (=range start). Arrival = Day0 = outside.
        var shifts = new[]
        {
            ShiftRow(Alice, TeamA, Day1, 9, 17),
            ShiftRow(Alice, TeamA, Day1.PlusDays(1), 9, 17),
        };
        var (repo, shiftMgmt, users) = BuildMocks(
            shifts: shifts,
            departments: [(TeamA, "TeamA")],
            playaNames: new Dictionary<Guid, string> { [Alice] = "Alice" });
        var sut = new VolunteerTrackingExportService(repo, shiftMgmt, users);

        var model = await sut.BuildAsync(BuildRequest(), ct: default);

        var cells = model.Groups[0].Humans[0].Cells;
        cells.Should().NotContain(c => c.Kind == CellKind.Arrival);
        cells[0].Kind.Should().Be(CellKind.Worked);
        cells[1].Kind.Should().Be(CellKind.Worked);
    }

    [HumansFact]
    public async Task MultiTeamDay_CellColoredByMaxHoursTeam()
    {
        // Alice on Day3: TeamA 3h + TeamB 5h → cell = TeamB color.
        var shifts = new[]
        {
            ShiftRow(Alice, TeamA, Day1.PlusDays(2), 9, 12),
            ShiftRow(Alice, TeamB, Day1.PlusDays(2), 13, 18),
        };
        var (repo, shiftMgmt, users) = BuildMocks(
            shifts: shifts,
            departments: [(TeamA, "TeamA"), (TeamB, "TeamB")],
            playaNames: new Dictionary<Guid, string> { [Alice] = "Alice" });
        var sut = new VolunteerTrackingExportService(repo, shiftMgmt, users);

        var model = await sut.BuildAsync(BuildRequest(), ct: default);

        var cellsDay3 = model.Groups.SelectMany(g => g.Humans).First().Cells[2];
        cellsDay3.Kind.Should().Be(CellKind.Worked);
        cellsDay3.TeamId.Should().Be(TeamB);
    }

    [HumansFact]
    public async Task ServiceTrustsRepoFilter_DoesNotReFilterByStatus()
    {
        // Whatever the repo returns is treated as authoritative.
        // The repo's integration test (Chunk 2) covers the actual WHERE clause.
        var shifts = new[] { ShiftRow(Alice, TeamA, Day1.PlusDays(2), 9, 17) };
        var (repo, shiftMgmt, users) = BuildMocks(
            shifts: shifts,
            departments: [(TeamA, "TeamA")],
            playaNames: new Dictionary<Guid, string> { [Alice] = "Alice" });
        var sut = new VolunteerTrackingExportService(repo, shiftMgmt, users);

        var model = await sut.BuildAsync(BuildRequest(), ct: default);

        model.Groups.Should().HaveCount(1);
        // The service does not call any status filter on the rows it receives.
        await repo.Received(1).GetConfirmedShiftsInRangeAsync(
            Arg.Any<Guid>(), Arg.Any<LocalDate>(), Arg.Any<LocalDate>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    private static readonly Guid Carol = Guid.Parse("c0000000-0000-0000-0000-000000000003");

    [HumansFact]
    public async Task GroupOrdering_ByTotalTeamHoursDescending_TieBreakOnName()
    {
        // Alice: TeamA 8h Day3.
        // Bob: TeamA 8h Day3, TeamA 8h Day4 (TeamA total = 24h).
        // Carol: TeamB 8h Day3, TeamB 8h Day4, TeamB 8h Day5 (TeamB total = 24h, ties with TeamA).
        // Tie → alphabetical → TeamA first.
        var shifts = new[]
        {
            ShiftRow(Alice, TeamA, Day1.PlusDays(2), 9, 17),
            ShiftRow(Bob,   TeamA, Day1.PlusDays(2), 9, 17),
            ShiftRow(Bob,   TeamA, Day1.PlusDays(3), 9, 17),
            ShiftRow(Carol, TeamB, Day1.PlusDays(2), 9, 17),
            ShiftRow(Carol, TeamB, Day1.PlusDays(3), 9, 17),
            ShiftRow(Carol, TeamB, Day1.PlusDays(4), 9, 17),
        };
        var (repo, shiftMgmt, users) = BuildMocks(
            shifts: shifts,
            departments: [(TeamA, "TeamA"), (TeamB, "TeamB")],
            playaNames: new Dictionary<Guid, string>
            {
                [Alice] = "Alice",
                [Bob] = "Bob",
                [Carol] = "Carol",
            });
        var sut = new VolunteerTrackingExportService(repo, shiftMgmt, users);

        var model = await sut.BuildAsync(BuildRequest(), ct: default);

        model.Groups.Should().HaveCount(2);
        model.Groups[0].TeamName.Should().Be("TeamA");  // tie → alpha
        model.Groups[1].TeamName.Should().Be("TeamB");
        model.Groups[0].Humans.Select(h => h.PlayaName).Should().Equal("Alice", "Bob");
        model.Groups[1].Humans.Select(h => h.PlayaName).Should().Equal("Carol");
    }

    private static readonly Guid Zara = Guid.Parse("d0000000-0000-0000-0000-000000000004");

    [HumansFact]
    public async Task WithinGroup_OrderedByArrivalDayAscending_TieBreakByName()
    {
        // Same team. Zara's first shift is Day2 → arrives Day1. Alice's first shift is Day4 → arrives Day3.
        // Even though "Alice" sorts before "Zara" alphabetically, Zara arrives earlier so appears first.
        var shifts = new[]
        {
            ShiftRow(Zara,  TeamA, Day1.PlusDays(1), 9, 17),
            ShiftRow(Alice, TeamA, Day1.PlusDays(3), 9, 17),
        };
        var (repo, shiftMgmt, users) = BuildMocks(
            shifts: shifts,
            departments: [(TeamA, "TeamA")],
            playaNames: new Dictionary<Guid, string>
            {
                [Alice] = "Alice",
                [Zara] = "Zara",
            });
        var sut = new VolunteerTrackingExportService(repo, shiftMgmt, users);

        var model = await sut.BuildAsync(BuildRequest(), ct: default);

        model.Groups.Should().HaveCount(1);
        model.Groups[0].Humans.Select(h => h.PlayaName).Should().Equal("Zara", "Alice");
    }

    /// <summary>Helper: build a ConfirmedShiftRow with start/end specified as event-local hours on a given local date.</summary>
    private static ConfirmedShiftRow ShiftRow(Guid userId, Guid teamId, LocalDate localDate, int startHourLocal, int endHourLocal)
    {
        var zone = DateTimeZoneProviders.Tzdb["Europe/Madrid"];
        var startInstant = (localDate + LocalTime.FromHourMinuteSecondTick(startHourLocal, 0, 0, 0)).InZoneStrictly(zone).ToInstant();
        var endInstant = (localDate + LocalTime.FromHourMinuteSecondTick(endHourLocal, 0, 0, 0)).InZoneStrictly(zone).ToInstant();
        return new ConfirmedShiftRow(userId, teamId, startInstant, endInstant);
    }
}
