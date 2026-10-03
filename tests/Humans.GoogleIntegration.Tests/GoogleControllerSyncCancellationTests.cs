using Humans.GoogleIntegration.Contracts;
using AwesomeAssertions;
using Humans.Users.Contracts;
using Humans.GoogleIntegration.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Humans.GoogleIntegration.Services;
using Humans.Teams.Contracts;

namespace Humans.GoogleIntegration.Tests;

/// <summary>
/// nobodies-collective/Humans#950 — an admin closing the tab must not tear a
/// Google Workspace reconcile in half. The execute paths must hand the sync
/// service a token that is not the request's, so an already-aborted request
/// still reaches the service with a live token.
/// </summary>
/// <remarks>
/// Sibling enforcement is the HUM0033 analyzer, which fails the build on any
/// new state-changing action that passes a request-scoped token to an
/// <c>[ExternalWrite]</c> method. This test pins the observable behaviour of the
/// two actions the issue named.
/// </remarks>
public class GoogleControllerSyncCancellationTests
{
    private readonly IUserServiceRead _users = Substitute.For<IUserServiceRead>();
    private readonly ITeamResourceService _resources = Substitute.For<ITeamResourceService>();
    private readonly IGoogleAdminService _admin = Substitute.For<IGoogleAdminService>();
    private readonly IGoogleSyncService _syncService = Substitute.For<IGoogleSyncService>();
    private readonly IGoogleGroupSync _groupSync = Substitute.For<IGoogleGroupSync>();

    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task AccountsAndOutbox_ForwardTheBrowserTokenToEveryRead(bool outbox)
    {
        var controller = BuildSut(alreadyAbortedRequest: true);
        var ct = controller.HttpContext.RequestAborted;
        if (outbox)
        {
            var userId = Guid.NewGuid();
            var teamId = Guid.NewGuid();
            _syncService.GetRecentOutboxEventsAsync(200, Arg.Any<CancellationToken>())
                .Returns([new GoogleSyncOutboxEventSnapshot(Guid.NewGuid(), "test", teamId, userId, default, null, 0, null, false)]);
            var teams = Substitute.For<ITeamServiceRead>();
            teams.GetTeamsAsync(Arg.Any<CancellationToken>()).Returns(new Dictionary<Guid, TeamInfo>());
            _resources.GetResourcesByTeamIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
                .Returns(new Dictionary<Guid, IReadOnlyList<GoogleResourceSnapshot>>());

            (await controller.SyncOutbox(teams, Substitute.For<IGoogleDriveActivityClient>())).Should().BeOfType<ViewResult>();

            await _syncService.Received(1).GetRecentOutboxEventsAsync(200, ct);
            await _users.Received(1).GetUserInfoAsync(userId, ct);
            await teams.Received(1).GetTeamsAsync(ct);
            await _resources.Received(1).GetResourcesByTeamIdsAsync(Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(teamId)), ct);
        }
        else
        {
            _admin.GetWorkspaceAccountListAsync(Arg.Any<CancellationToken>())
                .Returns(new WorkspaceAccountListResult([], 0, 0, 0, 0, 0, 0, 0));
            (await controller.Accounts()).Should().BeOfType<ViewResult>();
            await _admin.Received(1).GetWorkspaceAccountListAsync(ct);
        }
    }

    [HumansFact]
    public async Task SyncExecute_does_not_forward_the_aborted_request_token()
    {
        var received = CaptureSingleResourceToken();
        var controller = BuildSut(alreadyAbortedRequest: true);

        await controller.SyncExecute(Guid.NewGuid());

        received.Value.IsCancellationRequested.Should().BeFalse(
            "an aborted browser request must not abort a write-performing reconcile");
    }

    [HumansFact]
    public async Task SyncExecuteAll_does_not_forward_the_aborted_request_token_for_drive()
    {
        var received = CaptureByTypeToken();
        var controller = BuildSut(alreadyAbortedRequest: true);

        await controller.SyncExecuteAll(GoogleResourceType.DriveFolder);

        received.Value.IsCancellationRequested.Should().BeFalse();
    }

    [HumansFact]
    public async Task SyncExecuteAll_does_not_forward_the_aborted_request_token_for_groups()
    {
        var received = new Box();
        _groupSync
            .ReconcileAllAsync(SyncAction.Execute, Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                received.Value = call.Arg<CancellationToken>();
                return Task.FromResult(new SyncPreviewResult { Diffs = [] });
            });

        var controller = BuildSut(alreadyAbortedRequest: true);

        await controller.SyncExecuteAll(GoogleResourceType.Group);

        received.Value.IsCancellationRequested.Should().BeFalse();
    }

    [HumansFact]
    public async Task SyncPreview_still_forwards_the_request_token_because_it_only_reads()
    {
        var received = new Box();
        _syncService
            .SyncResourcesByTypeAsync(GoogleResourceType.DriveFolder, SyncAction.Preview, Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                received.Value = call.Arg<CancellationToken>();
                return Task.FromResult(new SyncPreviewResult { Diffs = [] });
            });

        var controller = BuildSut(alreadyAbortedRequest: true);

        await controller.SyncPreview(GoogleResourceType.DriveFolder);

        received.Value.IsCancellationRequested.Should().BeTrue(
            "abandoning a read for a page nobody is viewing is the desired behaviour");
    }

    private Box CaptureSingleResourceToken()
    {
        var box = new Box();
        _syncService
            .SyncSingleResourceAsync(Arg.Any<Guid>(), SyncAction.Execute, Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                box.Value = call.Arg<CancellationToken>();
                return Task.FromResult(new ResourceSyncDiff());
            });
        return box;
    }

    private Box CaptureByTypeToken()
    {
        var box = new Box();
        _syncService
            .SyncResourcesByTypeAsync(GoogleResourceType.DriveFolder, SyncAction.Execute, Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                box.Value = call.Arg<CancellationToken>();
                return Task.FromResult(new SyncPreviewResult { Diffs = [] });
            });
        return box;
    }

    private GoogleController BuildSut(bool alreadyAbortedRequest)
    {
        var controller = new GoogleController(
            _users,
            _syncService,
            _groupSync,
            _resources,
            Substitute.For<IEmailProvisioningService>(),
            _admin,
            NullLogger<GoogleController>.Instance);

        var aborted = new CancellationTokenSource();
        if (alreadyAbortedRequest)
            aborted.Cancel();

        var http = new DefaultHttpContext { RequestAborted = aborted.Token };
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        controller.TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>());

        return controller;
    }

    /// <summary>Mutable capture cell — <c>CancellationToken</c> cannot be a captured <c>out</c>.</summary>
    private sealed class Box
    {
        public CancellationToken Value { get; set; }
    }
}
