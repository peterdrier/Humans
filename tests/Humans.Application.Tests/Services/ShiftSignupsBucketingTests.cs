using AwesomeAssertions;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Models;
using Humans.Web.ViewComponents;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;

namespace Humans.Application.Tests.Services;

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

    [Fact]
    public async Task InProgressConfirmedShift_BucketedAsUpcoming()
    {
        // Shift started at 08:00 (4h ago), ends at 16:00 (4h from now) → still in progress
        var signup = MakeSignup(SignupStatus.Confirmed, dayOffset: 0, startHour: 8, durationHours: 8);
        var model = await RunComponent([signup]);

        model.Upcoming.Should().HaveCount(1);
        model.Past.Should().BeEmpty();
    }

    [Fact]
    public async Task EndedConfirmedShift_BucketedAsPast()
    {
        // Shift started at 06:00, ended at 10:00 (2h ago) → past
        var signup = MakeSignup(SignupStatus.Confirmed, dayOffset: 0, startHour: 6, durationHours: 4);
        var model = await RunComponent([signup]);

        model.Past.Should().HaveCount(1);
        model.Upcoming.Should().BeEmpty();
    }

    [Fact]
    public async Task FutureConfirmedShift_BucketedAsUpcoming()
    {
        // Shift starts tomorrow at 08:00 → future
        var signup = MakeSignup(SignupStatus.Confirmed, dayOffset: 1, startHour: 8, durationHours: 4);
        var model = await RunComponent([signup]);

        model.Upcoming.Should().HaveCount(1);
        model.Past.Should().BeEmpty();
    }

    [Fact]
    public async Task PendingShift_BucketedAsPending()
    {
        var signup = MakeSignup(SignupStatus.Pending, dayOffset: 1, startHour: 8, durationHours: 4);
        var model = await RunComponent([signup]);

        model.Pending.Should().HaveCount(1);
        model.Upcoming.Should().BeEmpty();
        model.Past.Should().BeEmpty();
    }

    [Fact]
    public async Task NoShowShift_BucketedAsPast()
    {
        var signup = MakeSignup(SignupStatus.NoShow, dayOffset: 0, startHour: 6, durationHours: 4);
        var model = await RunComponent([signup]);

        model.Past.Should().HaveCount(1);
    }

    [Fact]
    public async Task BailedShift_BucketedAsPast()
    {
        var signup = MakeSignup(SignupStatus.Bailed, dayOffset: 0, startHour: 6, durationHours: 4);
        var model = await RunComponent([signup]);

        model.Past.Should().HaveCount(1);
    }

    [Fact]
    public async Task ServiceError_ReturnsEmptyModel()
    {
        var signupService = Substitute.For<IShiftSignupService>();
        var shiftMgmt = Substitute.For<IShiftManagementService>();
        shiftMgmt.GetActiveAsync()
            .Returns(Task.FromException<EventSettings?>(new InvalidOperationException("DB down")));

        var model = await RunComponent([], shiftMgmt, signupService);

        model.Upcoming.Should().BeEmpty();
        model.Pending.Should().BeEmpty();
        model.Past.Should().BeEmpty();
    }

    private async Task<ShiftSignupsViewModel> RunComponent(
        List<ShiftSignup> signups,
        IShiftManagementService? shiftMgmt = null,
        IShiftSignupService? signupService = null)
    {
        var callerProvidedMocks = shiftMgmt is not null;
        signupService ??= Substitute.For<IShiftSignupService>();
        shiftMgmt ??= Substitute.For<IShiftManagementService>();

        if (!callerProvidedMocks)
        {
            shiftMgmt.GetActiveAsync().Returns(TestEvent);
            signupService.GetByUserAsync(_userId, TestEvent.Id)
                .Returns(signups);
        }

        var clock = new FakeClock(TestNow);
        var component = new ShiftSignupsViewComponent(
            signupService, shiftMgmt, clock,
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
        var team = new Team { Id = Guid.NewGuid(), Name = "Test Dept" };
        var rota = new Rota { Id = Guid.NewGuid(), Team = team, Priority = ShiftPriority.Normal };
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
