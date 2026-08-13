using Humans.Shifts.Domain;
using Humans.Shifts.Services;
using AwesomeAssertions;
using Humans.Shifts.Services.Dtos;
using Humans.Shifts.Contracts;
using Humans.Teams.Contracts;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Shifts.Models;
using Humans.Shifts.ViewComponents;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace Humans.Shifts.Tests.Services;

public class ShiftSignupsBucketingTests
{
    // Event starts July 1 at midnight UTC. Test clock is July 1 12:00 UTC (mid-event).
    private static readonly Instant TestNow = Instant.FromUtc(2026, 7, 1, 12, 0);

    private static readonly BurnSettingsInfo TestEvent = new(
        Id: Guid.NewGuid(),
        EventName: "Test Burn",
        Year: 2026,
        TimeZoneId: "UTC",
        GateOpeningDate: new LocalDate(2026, 7, 1),
        BuildStartOffset: -14,
        EventEndOffset: 6,
        StrikeEndOffset: 9,
        FirstCrewStartOffset: -25,
        SetupWeekStartOffset: -16,
        PreEventWeekStartOffset: -9,
        FinishingWeekendStartOffset: -4,
        EarlyEntryCapacity: new Dictionary<int, int>(),
        BarriosEarlyEntryAllocation: null,
        EarlyEntryClose: null,
        IsShiftBrowsingOpen: true);

    private readonly Guid _userId = Guid.NewGuid();

    [HumansFact]
    public async Task InProgressConfirmedShift_BucketedAsUpcoming()
    {
        // Shift started at 08:00 (4h ago), ends at 16:00 (4h from now) → still in progress
        var signup = MakeSignup(SignupStatus.Confirmed, dayOffset: 0, startHour: 8, durationHours: 8);
        var model = await RunComponent([signup]);

        model.Upcoming.Should().HaveCount(1);
        model.Past.Should().BeEmpty();
    }

    [HumansFact]
    public async Task EndedConfirmedShift_BucketedAsPast()
    {
        // Shift started at 06:00, ended at 10:00 (2h ago) → past
        var signup = MakeSignup(SignupStatus.Confirmed, dayOffset: 0, startHour: 6, durationHours: 4);
        var model = await RunComponent([signup]);

        model.Past.Should().HaveCount(1);
        model.Upcoming.Should().BeEmpty();
    }

    [HumansFact]
    public async Task FutureConfirmedShift_BucketedAsUpcoming()
    {
        // Shift starts tomorrow at 08:00 → future
        var signup = MakeSignup(SignupStatus.Confirmed, dayOffset: 1, startHour: 8, durationHours: 4);
        var model = await RunComponent([signup]);

        model.Upcoming.Should().HaveCount(1);
        model.Past.Should().BeEmpty();
    }

    [HumansFact]
    public async Task PendingShift_BucketedAsPending()
    {
        var signup = MakeSignup(SignupStatus.Pending, dayOffset: 1, startHour: 8, durationHours: 4);
        var model = await RunComponent([signup]);

        model.Pending.Should().HaveCount(1);
        model.Upcoming.Should().BeEmpty();
        model.Past.Should().BeEmpty();
    }

    [HumansFact]
    public async Task NoShowShift_BucketedAsPast()
    {
        var signup = MakeSignup(SignupStatus.NoShow, dayOffset: 0, startHour: 6, durationHours: 4);
        var model = await RunComponent([signup]);

        model.Past.Should().HaveCount(1);
    }

    [HumansFact]
    public async Task BailedShift_BucketedAsPast()
    {
        var signup = MakeSignup(SignupStatus.Bailed, dayOffset: 0, startHour: 6, durationHours: 4);
        var model = await RunComponent([signup]);

        model.Past.Should().HaveCount(1);
    }

    [HumansFact]
    public async Task ServiceError_ReturnsEmptyModel()
    {
        var shiftView = Substitute.For<IShiftView>();
        var burnSettings = Substitute.For<IBurnSettingsService>();
        burnSettings.GetActiveAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<BurnSettingsInfo?>(new InvalidOperationException("DB down")));

        var model = await RunComponent([], burnSettings, shiftView);

        model.Upcoming.Should().BeEmpty();
        model.Pending.Should().BeEmpty();
        model.Past.Should().BeEmpty();
    }

    private async Task<ShiftSignupsViewModel> RunComponent(
        List<ShiftSignupSummary> signups,
        IBurnSettingsService? burnSettings = null,
        IShiftView? shiftView = null)
    {
        var callerProvidedMocks = burnSettings is not null;
        shiftView ??= Substitute.For<IShiftView>();
        burnSettings ??= Substitute.For<IBurnSettingsService>();

        if (!callerProvidedMocks)
        {
            burnSettings.GetActiveAsync(Arg.Any<CancellationToken>()).Returns(TestEvent);
            shiftView.GetUserAsync(_userId, Arg.Any<CancellationToken>())
                .Returns(new ValueTask<ShiftUserSummary>(
                    ShiftFixtures.UserSummary(_userId, signups)));
        }

        var teamService = Substitute.For<ITeamService>();
        teamService.GetTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                var teamIds = signups.Select(s => s.TeamId).Distinct().ToList();
                IReadOnlyDictionary<Guid, TeamInfo> dict = teamIds.ToDictionary(
                    id => id,
                    id => new TeamInfo(
                        id, "Test Dept", null, "test-dept",
                        IsActive: true, IsSystemTeam: false, SystemTeamType: SystemTeamType.None,
                        RequiresApproval: false, IsPublicPage: false, IsHidden: false,
                        IsPromotedToDirectory: false, CreatedAt: Instant.MinValue,
                        Members: []));
                return dict;
            });

        var clock = new FakeClock(TestNow);
        var component = new ShiftSignupsViewComponent(
            shiftView, burnSettings, teamService, clock,
            NullLogger<ShiftSignupsViewComponent>.Instance);

        // Minimal ViewComponentContext for View() to work
        var httpContext = new DefaultHttpContext();
        var viewContext = new ViewContext
        {
            HttpContext = httpContext,
            ViewData = new ViewDataDictionary(new EmptyModelMetadataProvider(), new ModelStateDictionary())
        };
        component.ViewComponentContext = new ViewComponentContext { ViewContext = viewContext };

        var result = await component.InvokeAsync(_userId, ShiftSignupsViewMode.Self);
        var viewResult = (ViewViewComponentResult)result;
        return (ShiftSignupsViewModel)viewResult.ViewData!.Model!;
    }

    /// <summary>
    /// A flat signup on the event's gate-opening day plus <paramref name="dayOffset"/>,
    /// resolved in UTC to match <see cref="TestEvent"/>'s time zone. The component reads
    /// the leaf projection since the section's G5, so the absolute window is the fixture's
    /// job rather than the entity graph's (nobodies-collective/Humans#866).
    /// </summary>
    private static ShiftSignupSummary MakeSignup(SignupStatus status, int dayOffset, int startHour, int durationHours)
    {
        var date = new LocalDate(2026, 7, 1).PlusDays(dayOffset);
        return ShiftFixtures.Signup(
            status: status,
            date: date,
            windowStart: new LocalTime(startHour, 0),
            durationHours: durationHours);
    }
}
