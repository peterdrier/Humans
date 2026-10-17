using AwesomeAssertions;
using Humans.GoogleIntegration.Contracts;
using Humans.Base.Enums;
using Humans.GoogleIntegration.Controllers;
using Humans.GoogleIntegration.Models;
using NodaTime;
using Humans.GoogleIntegration.Services;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Humans.GoogleIntegration.Tests;

public sealed class GoogleControllerSyncAuditTests
{
    private readonly IUserServiceRead _users = Substitute.For<IUserServiceRead>();
    private readonly ITeamResourceService _resources = Substitute.For<ITeamResourceService>();
    private readonly IGoogleSyncLogService _syncLog = Substitute.For<IGoogleSyncLogService>();

    [HumansFact]
    public async Task Resource_WithUnknownId_ReturnsNotFound()
    {
        using var cancellation = new CancellationTokenSource();
        var id = Guid.NewGuid();
        var result = await BuildSut(cancellation.Token).Resource(id);

        result.Should().BeOfType<NotFoundResult>();
        await _resources.Received(1).GetResourceByIdAsync(id, cancellation.Token);
    }

    [HumansFact]
    public async Task Human_WithUnknownId_ReturnsNotFound()
    {
        using var cancellation = new CancellationTokenSource();
        var id = Guid.NewGuid();
        var result = await BuildSut(cancellation.Token).Human(id);

        result.Should().BeOfType<NotFoundResult>();
        await _users.Received(1).GetUserInfoAsync(id, cancellation.Token);
    }

    [HumansFact]
    public async Task ActivityLog_ConvertsInclusiveDatesToUtcRange_AndPassesFilters()
    {
        using var cancellation = new CancellationTokenSource();
        var views = new List<GoogleSyncLogView>();
        _syncLog.GetRecentAsync(default, default, default, default, default, default, default)
            .ReturnsForAnyArgs(views);

        var result = await BuildSut(cancellation.Token).ActivityLog(
            new LocalDate(2026, 10, 1), new LocalDate(2026, 10, 3), GoogleSyncLogAction.AccessRevoked,
            GoogleSyncSource.ManualSync, false, "Docs");

        await _syncLog.Received(1).GetRecentAsync(
            Instant.FromUtc(2026, 10, 1, 0, 0), Instant.FromUtc(2026, 10, 4, 0, 0),
            GoogleSyncLogAction.AccessRevoked, GoogleSyncSource.ManualSync, false, "Docs", cancellation.Token);
        var model = result.Should().BeOfType<ViewResult>().Subject.Model.Should().BeOfType<ActivityLogViewModel>().Subject;
        model.Entries.Should().BeSameAs(views);
        model.Resource.Should().Be("Docs");
    }

    private GoogleController BuildSut(CancellationToken token) => new(
        _users,
        Substitute.For<IGoogleSyncService>(),
        Substitute.For<IGoogleGroupSync>(),
        _resources,
        Substitute.For<IEmailProvisioningService>(),
        Substitute.For<IGoogleAdminService>(),
        _syncLog,
        NullLogger<GoogleController>.Instance)
    {
        ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestAborted = token } }
    };
}
