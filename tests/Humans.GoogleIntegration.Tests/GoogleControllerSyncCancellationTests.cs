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
    private readonly IGoogleSyncService _syncService = Substitute.For<IGoogleSyncService>();
    private readonly IGoogleGroupSync _groupSync = Substitute.For<IGoogleGroupSync>();

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
            Substitute.For<IUserServiceRead>(),
            _syncService,
            _groupSync,
            Substitute.For<ITeamResourceService>(),
            Substitute.For<IEmailProvisioningService>(),
            Substitute.For<IGoogleAdminService>(),
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
