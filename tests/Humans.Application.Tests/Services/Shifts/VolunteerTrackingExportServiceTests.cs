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
    private static readonly Guid TeamA   = Guid.Parse("11111111-0000-0000-0000-000000000000");
    private static readonly Guid TeamB   = Guid.Parse("22222222-0000-0000-0000-000000000000");

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
            ShiftRow(Alice, TeamA, "TeamA", Day1.PlusDays(2), 9, 17),
            ShiftRow(Alice, TeamA, "TeamA", Day1.PlusDays(3), 9, 17),
            ShiftRow(Alice, TeamA, "TeamA", Day1.PlusDays(4), 9, 17),
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

    /// <summary>Helper: build a ConfirmedShiftRow with start/end specified as event-local hours on a given local date.</summary>
    private static ConfirmedShiftRow ShiftRow(Guid userId, Guid teamId, string teamName, LocalDate localDate, int startHourLocal, int endHourLocal)
    {
        var zone = DateTimeZoneProviders.Tzdb["Europe/Madrid"];
        var startInstant = (localDate + LocalTime.FromHourMinuteSecondTick(startHourLocal, 0, 0, 0)).InZoneStrictly(zone).ToInstant();
        var endInstant = (localDate + LocalTime.FromHourMinuteSecondTick(endHourLocal, 0, 0, 0)).InZoneStrictly(zone).ToInstant();
        return new ConfirmedShiftRow(userId, teamId, teamName, startInstant, endInstant);
    }
}
