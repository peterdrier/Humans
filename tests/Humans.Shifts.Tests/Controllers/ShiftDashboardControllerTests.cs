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

    [HumansFact]
    public async Task Index_OrdersCoverageAndCoordinatorActivityForDisplay()
    {
        var settings = Event;
        var empty = new PeriodStaffing(0, 0, 0, 0, 0);
        SubgroupStaffingRow Subgroup(string name, bool direct, int total, int filled) =>
            new(Guid.NewGuid(), name, null, direct, 0, 0, total, filled, total - filled,
                empty, empty, empty);
        DepartmentStaffingRow Department(string name, int total, int filled,
            IReadOnlyList<SubgroupStaffingRow> subgroups) =>
            new(Guid.NewGuid(), name, null, 0, 0, total, filled, total - filled,
                empty, empty, empty, subgroups);
        var original = new DashboardOverview(0, 0, 0, 0, new PeriodBreakdown(0, 0, 0), 0, 0, 0, 0,
        [
            Department("Filled", 3, 3, []),
            Department("Zero", 0, 0, []),
            Department("Empty", 3, 0,
            [
                Subgroup("Power", false, 3, 2),
                Subgroup("Zero", false, 0, 0),
                Subgroup("Plumbing", false, 3, 0),
                Subgroup("Direct", true, 3, 3)
            ])
        ]);
        var recent = Instant.FromUtc(2026, 7, 1, 12, 0);
        var older = recent.Minus(Duration.FromDays(20));
        CoordinatorActivityRow Activity(string name, IReadOnlyList<CoordinatorLogin> coordinators,
            params CoordinatorActivityRow[] children) =>
            new(Guid.NewGuid(), name, coordinators, 1, 1 + children.Sum(c => c.AggregatePendingCount), children);
        var activity = new[]
        {
            Activity("Recent", [new(Guid.NewGuid(), recent)]),
            Activity("Parent", [new(Guid.NewGuid(), recent)],
                Activity("Recent child", [new(Guid.NewGuid(), recent)]),
                Activity("Old child", [new(Guid.NewGuid(), recent), new(Guid.NewGuid(), older)])),
            Activity("Z Never", [new(Guid.NewGuid(), null)]),
            Activity("A Empty", [])
        };
        _management.GetCoordinatorActivityAsync(settings.Id, null, null, Arg.Any<CancellationToken>())
            .Returns(activity);
        _settings.GetActiveAsync(Arg.Any<CancellationToken>()).Returns(settings);
        _management.GetStaffingSnapshotAsync(settings.Id, null, null, null, Arg.Any<CancellationToken>())
            .Returns(ShiftStaffingSnapshot.Empty);
        _management.GetDashboardOverviewAsync(settings.Id, null, null, Arg.Any<CancellationToken>())
            .Returns(original);
        _management.GetCoverageHeatmapAsync(settings.Id, null, null, Arg.Any<CancellationToken>())
            .Returns(new CoverageHeatmap([], []));

        var result = Assert.IsType<ViewResult>(await Controller().Index(
            null, null, null, null, null, null, null, TestContext.Current.CancellationToken));
        var model = Assert.IsType<ShiftDashboardViewModel>(result.Model);
        Assert.Equal(new[] { "Empty", "Zero", "Filled" },
            model.Overview!.Departments.Select(d => d.DepartmentName), StringComparer.Ordinal);
        Assert.Equal(new[] { "Direct", "Plumbing", "Zero", "Power" },
            model.Overview.Departments[0].Subgroups.Select(s => s.Name), StringComparer.Ordinal);
        Assert.Equal(new[] { "Filled", "Zero", "Empty" }, original.Departments.Select(d => d.DepartmentName), StringComparer.Ordinal);
        Assert.Equal(new[] { "A Empty", "Z Never", "Parent", "Recent" },
            model.CoordinatorActivity.Select(r => r.TeamName), StringComparer.Ordinal);
        var parent = model.CoordinatorActivity[2];
        Assert.Equal(new[] { "Old child", "Recent child" }, parent.Subgroups.Select(r => r.TeamName), StringComparer.Ordinal);
        Assert.Equal(new[] { older, recent }, parent.Subgroups[0].Coordinators.Select(c => c.LastLoginAt!.Value));
        Assert.Equal(activity[1].AggregatePendingCount, parent.AggregatePendingCount);
        Assert.Equal(new[] { "Recent", "Parent", "Z Never", "A Empty" },
            activity.Select(r => r.TeamName), StringComparer.Ordinal);
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
