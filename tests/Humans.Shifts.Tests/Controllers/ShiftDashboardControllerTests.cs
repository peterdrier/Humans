using Humans.Cantina.Contracts;
using Humans.Shifts.Contracts;
using Humans.Shifts.Controllers;
using Humans.Shifts.Domain;
using Humans.Shifts.Helpers;
using Humans.Shifts.Models;
using Humans.Shifts.Services;
using Humans.Shifts.Services.Dtos;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;
using Xunit;

namespace Humans.Shifts.Tests.Controllers;

public class ShiftDashboardControllerTests
{
    private readonly IBurnSettingsService _settings = Substitute.For<IBurnSettingsService>();
    private readonly IShiftManagement _management = Substitute.For<IShiftManagement>();
    private readonly IShiftSignupService _signups = Substitute.For<IShiftSignupService>();
    private readonly IUserService _users = Substitute.For<IUserService>();

    private static BurnSettingsInfo Event => new(
        Guid.NewGuid(), "Event", 2026, "Europe/Madrid", new LocalDate(2026, 7, 9),
        -7, 4, 6, -25, -16, -9, -4, new Dictionary<int, int>(), null, null, true);

    [HumansFact]
    public async Task PostEventStats_ForwardsRequestCancellationToBothReads()
    {
        using var cancellation = new CancellationTokenSource();
        var settings = Event;
        _settings.GetActiveAsync(Arg.Any<CancellationToken>()).Returns(settings);
        _management.GetPostEventStatsAsync(settings.Id, Arg.Any<CancellationToken>())
            .Returns(new PostEventStats(0, 0, 0, 0, []));

        Assert.IsType<ViewResult>(await Controller().PostEventStats(cancellation.Token));

        await _settings.Received(1).GetActiveAsync(cancellation.Token);
        await _management.Received(1).GetPostEventStatsAsync(settings.Id, cancellation.Token);
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VolunteerSearch_PropagatesCancellationDuringShiftOrBuilderReads(bool duringBuilder)
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var shiftId = Guid.NewGuid();
        if (duringBuilder)
        {
            var settings = Event;
            var rotaId = Guid.NewGuid();
            _management.GetShiftByIdAsync(shiftId, Arg.Any<CancellationToken>()).Returns(new ShiftWithRota(
                new ShiftInfo(shiftId, rotaId, null, 0, new LocalTime(10, 0), 2, 0, 5, false, false, false),
                new RotaInfo(rotaId, settings.Id, Guid.NewGuid(), "Rota", null, null,
                    ShiftPriority.Normal, SignupPolicy.Public, RotaPeriod.Event, true, [])));
            _settings.GetActiveAsync(Arg.Any<CancellationToken>())
                .Returns(Task.FromCanceled<BurnSettingsInfo?>(cancellation.Token));
        }
        else
        {
            _management.GetShiftByIdAsync(shiftId, Arg.Any<CancellationToken>())
                .Returns(Task.FromCanceled<ShiftWithRota?>(cancellation.Token));
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Controller().SearchVolunteers(shiftId, "ann", cancellation.Token));

        await _management.Received(1).GetShiftByIdAsync(shiftId, cancellation.Token);
        if (duringBuilder) await _settings.Received(1).GetActiveAsync(cancellation.Token);
    }

    private ShiftDashboardController Controller() => new(
        _settings, _management, _signups, _users,
        new ShiftDashboardPageBuilder(_management, Substitute.For<IWebHostEnvironment>(), SystemClock.Instance),
        new ShiftVolunteerSearchBuilder(_settings, _users, Substitute.For<ICantinaService>(),
            Substitute.For<IShiftRowView>(), _signups, Substitute.For<IVolunteerTrackingManagement>()),
        NullLogger<ShiftDashboardController>.Instance)
    {
        ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
    };
}
