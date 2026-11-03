using System.Security.Claims;
using Humans.Base.Enums;
using Humans.GoogleIntegration.Contracts;
using AwesomeAssertions;
using Humans.Users.Contracts;
using Humans.GoogleIntegration.Controllers;
using Humans.GoogleIntegration.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Humans.GoogleIntegration.Services;
using Humans.Teams.Contracts;

namespace Humans.GoogleIntegration.Tests;

/// <summary>
/// An admin closing the tab must not tear a Workspace reconcile in half: the execute paths hand
/// the sync service a token that is not the request's.
/// </summary>
public class GoogleControllerSyncCancellationTests
{
    private readonly ILogger<GoogleController> _logger = Substitute.For<ILogger<GoogleController>>();
    private readonly IUserService _users = Substitute.For<IUserService>();
    private readonly ITeamResourceManagement _resources = Substitute.For<ITeamResourceManagement>();
    private readonly IGoogleAdminService _admin = Substitute.For<IGoogleAdminService>();
    private readonly IGoogleSyncManagement _syncService = Substitute.For<IGoogleSyncManagement>();
    private readonly IGoogleGroupSync _groupSync = Substitute.For<IGoogleGroupSync>();
    private readonly IEmailProvisioningService _provisioning = Substitute.For<IEmailProvisioningService>();

    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task DiagnosticChecks_ForwardBrowserTokenToReadOnlyDetection(bool groupSettings)
    {
        var controller = BuildSut(alreadyAbortedRequest: true);
        _admin.DetectEmailRenamesAsync(Arg.Any<CancellationToken>()).Returns(new EmailRenameDetectionResult());
        _syncService.CheckGroupSettingsAsync(Arg.Any<CancellationToken>()).Returns(new GroupSettingsDriftResult());

        (await (groupSettings ? controller.CheckGroupSettings() : controller.CheckEmailRenames()))
            .Should().BeOfType<RedirectToActionResult>().Which.ActionName.Should().Be(
                groupSettings ? nameof(GoogleController.GroupSettingsResults) : nameof(GoogleController.EmailRenames));

        if (groupSettings)
            await _syncService.Received(1).CheckGroupSettingsAsync(controller.HttpContext.RequestAborted);
        else
            await _admin.Received(1).DetectEmailRenamesAsync(controller.HttpContext.RequestAborted);
    }

    [HumansTheory]
    [Xunit.InlineData(false, false)]
    [Xunit.InlineData(false, true)]
    [Xunit.InlineData(true, false)]
    [Xunit.InlineData(true, true)]
    public async Task DiagnosticChecks_SeparateCallerAndDependencyCancellation(bool groupSettings, bool callerAborted)
    {
        var controller = BuildSut(alreadyAbortedRequest: callerAborted);
        var failure = new OperationCanceledException(controller.HttpContext.RequestAborted);
        _admin.DetectEmailRenamesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<EmailRenameDetectionResult>(failure));
        _syncService.CheckGroupSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<GroupSettingsDriftResult>(failure));
        Func<Task<IActionResult>> act = groupSettings ? controller.CheckGroupSettings : controller.CheckEmailRenames;
        if (callerAborted)
            (await act.Should().ThrowAsync<OperationCanceledException>()).Which.Should().BeSameAs(failure);
        else
            (await act()).Should().BeOfType<RedirectToActionResult>().Which.ActionName.Should().Be(nameof(GoogleController.Index));

        var resultKey = groupSettings ? Humans.Base.Constants.TempDataKeys.GroupSettingsResult : Humans.Base.Constants.TempDataKeys.EmailRenameResult;
        controller.TempData.ContainsKey(resultKey).Should().BeFalse();
        var args = _logger.ReceivedCalls().Should().ContainSingle().Subject.GetArguments();
        args[0].Should().Be(callerAborted ? LogLevel.Warning : LogLevel.Error);
        args[3].Should().BeSameAs(callerAborted ? null : failure);
    }

    [HumansFact]
    public async Task AllGroups_ForwardsBrowserTokenToGroupAndTeamReads()
    {
        var controller = BuildSut(alreadyAbortedRequest: true);
        var ct = controller.HttpContext.RequestAborted;
        _syncService.GetAllDomainGroupsAsync(Arg.Any<CancellationToken>()).Returns(new AllGroupsResult());
        _admin.GetActiveTeamsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<TeamSummary>());

        (await controller.AllGroups()).Should().BeOfType<ViewResult>();

        await _syncService.Received(1).GetAllDomainGroupsAsync(ct);
        await _admin.Received(1).GetActiveTeamsAsync(ct);
    }

    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task AllGroups_SeparatesCallerAndDependencyCancellation(bool callerAborted)
    {
        var controller = BuildSut(alreadyAbortedRequest: callerAborted);
        var failure = new OperationCanceledException("Group listing cancelled", controller.HttpContext.RequestAborted);
        _syncService.GetAllDomainGroupsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<AllGroupsResult>(failure));
        var act = () => controller.AllGroups();
        if (callerAborted) (await act.Should().ThrowAsync<OperationCanceledException>()).Which.Should().BeSameAs(failure);
        else (await act()).Should().BeOfType<RedirectToActionResult>().Which.ActionName.Should().Be(nameof(GoogleController.Index));

        var args = _logger.ReceivedCalls().Should().ContainSingle().Subject.GetArguments();
        args[0].Should().Be(callerAborted ? LogLevel.Warning : LogLevel.Error);
        args[3].Should().BeSameAs(callerAborted ? null : failure);
    }

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
            _users.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
                .Returns(new Dictionary<Guid, UserInfo>());
            var teams = Substitute.For<ITeamService>();
            teams.GetTeamsAsync(Arg.Any<CancellationToken>()).Returns(new Dictionary<Guid, TeamInfo>());
            _resources.GetResourcesByTeamIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
                .Returns(new Dictionary<Guid, IReadOnlyList<GoogleResourceSnapshot>>());

            (await controller.SyncOutbox(teams, Substitute.For<IGoogleDriveActivityClient>())).Should().BeOfType<ViewResult>();

            await _syncService.Received(1).GetRecentOutboxEventsAsync(200, ct);
            await _users.Received(1).GetUserInfosAsync(
                Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 1 && ids.Contains(userId)), ct);
            await teams.Received(1).GetTeamsAsync(ct);
            await _resources.Received(1).GetResourcesByTeamIdsAsync(Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(teamId)), ct);
        }
        else
        {
            var second = new WorkspaceAccountInfo("Zara@nobodies.team", "Zara", "", false,
                DateTime.UnixEpoch, null, null, null, false, false);
            var first = second with { PrimaryEmail = "aaron@nobodies.team" };
            _admin.GetWorkspaceAccountListAsync(Arg.Any<CancellationToken>())
                .Returns(new WorkspaceAccountListResult([second, first], 2, 2, 0, 0, 2, 0, 2));
            var view = (await controller.Accounts()).Should().BeOfType<ViewResult>().Subject;
            var model = view.Model.Should().BeOfType<WorkspaceEmailListViewModel>().Subject;
            model.Accounts.Select(a => a.PrimaryEmail).Should().Equal(first.PrimaryEmail, second.PrimaryEmail);
            model.TotalAccounts.Should().Be(2);
            model.MissingTwoFactorCount.Should().Be(2);
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
    public async Task SyncPreview_does_not_forward_the_request_token_because_it_can_write()
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

        received.Value.IsCancellationRequested.Should().BeFalse(
            "a preview stamps resource errors, so it is a POST and finishes once started (HUM0038)");
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

    [HumansTheory]
    [Xunit.InlineData(0)]
    [Xunit.InlineData(1)]
    [Xunit.InlineData(2)]
    [Xunit.InlineData(3)]
    [Xunit.InlineData(4)]
    [Xunit.InlineData(5)]
    [Xunit.InlineData(6)]
    [Xunit.InlineData(7)]
    [Xunit.InlineData(8)]
    [Xunit.InlineData(9)]
    [Xunit.InlineData(10)]
    public async Task Account_preflight_cancellation_prevents_admin_mutations(int action)
    {
        var actorId = Guid.NewGuid();
        var controller = BuildSut(alreadyAbortedRequest: true);
        controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, actorId.ToString())], "test"));
        _users.GetUserInfoAsync(actorId, Arg.Any<CancellationToken>()).Returns(call =>
        {
            call.Arg<CancellationToken>().Should().Be(controller.HttpContext.RequestAborted);
            call.Arg<CancellationToken>().ThrowIfCancellationRequested();
            return new ValueTask<UserInfo?>(UserInfo.Create(new User { Id = actorId }, [], [], null));
        });
        var settings = Substitute.For<ISyncSettingsService>();
        var targetId = Guid.NewGuid();
        Func<Task<IActionResult>> invoke = action switch
        {
            0 => () => controller.UpdateSyncSetting(settings, SyncServiceType.GoogleDrive, SyncMode.None),
            1 => () => controller.ProvisionEmail(targetId, "member"),
            2 => () => controller.ProvisionAccount(new ProvisionWorkspaceAccountModel { EmailPrefix = "member" }),
            3 => () => controller.SuspendAccount("member@example.org"),
            4 => () => controller.ReactivateAccount("member@example.org"),
            5 => () => controller.ResetPassword("member@example.org"),
            6 => () => controller.ResetPasswordAndGenerate2Fa("member@example.org"),
            7 => () => controller.LinkAccount("member@example.org", targetId),
            8 => () => controller.RequeueOutboxEvent(targetId),
            9 => () => controller.RequeueAllFailedOutboxEvents(),
            _ => () => controller.RerunGoogleSync(targetId),
        };

        await invoke.Should().ThrowAsync<OperationCanceledException>();
        settings.ReceivedCalls().Should().BeEmpty();
        _admin.ReceivedCalls().Should().BeEmpty();
        _provisioning.ReceivedCalls().Should().BeEmpty();
        _syncService.ReceivedCalls().Should().BeEmpty();
        controller.TempData.Should().BeEmpty();
    }

    private GoogleController BuildSut(bool alreadyAbortedRequest)
    {
        var controller = new GoogleController(
            _users,
            _syncService,
            _groupSync,
            _resources,
            _provisioning,
            _admin,
            Substitute.For<IGoogleIntegrationService>(),
            Substitute.For<IGoogleSyncLogService>(),
            _logger);

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
