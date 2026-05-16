using AwesomeAssertions;
using Humans.Application.DTOs.Shifts;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Teams;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Models;
using Humans.Web.ViewComponents;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace Humans.Application.Tests.Services.Shifts;

public class ShiftSignupsBucketingTests
{
    // Event starts July 1 at midnight UTC. Test clock is July 1 12:00 UTC (mid-event).
    private static readonly Instant TestNow = Instant.FromUtc(2026, 7, 1, 12, 0);

    private static readonly EventSettings TestEvent = new()
    {
        Id = Guid.NewGuid(),
        GateOpeningDate = new LocalDate(2026, 7, 1),
        TimeZoneId = "UTC"
    };

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
        var shiftMgmt = Substitute.For<IShiftManagementService>();
        shiftMgmt.GetActiveAsync()
            .Returns(Task.FromException<EventSettings?>(new InvalidOperationException("DB down")));

        var model = await RunComponent([], shiftMgmt, shiftView);

        model.Upcoming.Should().BeEmpty();
        model.Pending.Should().BeEmpty();
        model.Past.Should().BeEmpty();
    }

    private async Task<ShiftSignupsViewModel> RunComponent(
        List<ShiftSignup> signups,
        IShiftManagementService? shiftMgmt = null,
        IShiftView? shiftView = null)
    {
        var callerProvidedMocks = shiftMgmt is not null;
        shiftView ??= Substitute.For<IShiftView>();
        shiftMgmt ??= Substitute.For<IShiftManagementService>();

        if (!callerProvidedMocks)
        {
            shiftMgmt.GetActiveAsync().Returns(TestEvent);
            shiftView.GetUserAsync(_userId, Arg.Any<CancellationToken>())
                .Returns(new ValueTask<ShiftUserView>(new ShiftUserView(
                    _userId,
                    Profile: null,
                    Availability: null,
                    BuildStatus: null,
                    TagPreferences: [],
                    Signups: signups)));
        }

        var teamService = Substitute.For<ITeamService>();
        teamService.GetTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                var teamIds = signups.Select(s => s.Shift.Rota.TeamId).Distinct().ToList();
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
            shiftView, shiftMgmt, teamService, clock,
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

    private static ShiftSignup MakeSignup(SignupStatus status, int dayOffset, int startHour, int durationHours)
    {
        var rota = new Rota { Id = Guid.NewGuid(), TeamId = Guid.NewGuid(), Priority = ShiftPriority.Normal };
        var shift = new Shift
        {
            Id = Guid.NewGuid(),
            Rota = rota,
            DayOffset = dayOffset,
            StartTime = new LocalTime(startHour, 0),
            Duration = Duration.FromHours(durationHours),
            MinVolunteers = 1,
            MaxVolunteers = 5
        };

        return new ShiftSignup
        {
            Id = Guid.NewGuid(),
            Shift = shift,
            Status = status
        };
    }
}
