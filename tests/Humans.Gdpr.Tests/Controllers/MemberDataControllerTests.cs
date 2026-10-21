using Xunit;
using AwesomeAssertions;
using Humans.Base;
using Humans.Base.Constants;
using Humans.Gdpr.Contracts;
using Humans.Gdpr.Controllers;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace Humans.Gdpr.Tests.Controllers;

/// <summary>A member's own export and deletion-request actions on the My Data page.</summary>
public class MemberDataControllerTests
{
    private readonly Guid _userId = Guid.NewGuid();
    private readonly IUserService _users = Substitute.For<IUserService>();
    private readonly IGdprService _gdpr = Substitute.For<IGdprService>();
    private readonly IAccountDeletionService _deletion = Substitute.For<IAccountDeletionService>();

    private MemberDataController Create(bool signedIn = true)
    {
        var localizer = Substitute.For<IStringLocalizer<GdprResource>>();
        localizer[Arg.Any<string>()].Returns(ci => new LocalizedString(ci.Arg<string>(), ci.Arg<string>()));
        var shared = Substitute.For<IStringLocalizer<SharedResource>>();
        shared[Arg.Any<string>()].Returns(ci => new LocalizedString(ci.Arg<string>(), ci.Arg<string>()));
        _users.GetUserInfoAsync(_userId, Arg.Any<CancellationToken>()).Returns(ControllerTestHost.Info(_userId));
        return new MemberDataController(
            _users, _gdpr, _deletion, new FakeClock(Instant.FromUtc(2026, 10, 3, 0, 0)),
            NullLogger<MemberDataController>.Instance, localizer, shared)
            .Wire(signedIn ? _userId : null);
    }

    [HumansFact]
    public async Task DownloadData_returns_the_json_export_of_the_signed_in_member_only()
    {
        _gdpr.ExportForUserAsync(_userId, Arg.Any<CancellationToken>())
            .Returns(new GdprExport("2026-10-03T00:00:00Z", _userId, [], new Dictionary<string, object?>(StringComparer.Ordinal)));

        var result = await Create().DownloadData(TestContext.Current.CancellationToken);

        var file = result.Should().BeOfType<FileContentResult>().Subject;
        file.ContentType.Should().Be("application/json");
        file.FileDownloadName.StartsWith("nobodies-profiles-export-", StringComparison.Ordinal).Should().BeTrue();
        await _gdpr.Received(1).ExportForUserAsync(_userId, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task DownloadData_without_a_signed_in_member_is_not_found_and_exports_nothing()
    {
        var result = await Create(signedIn: false).DownloadData(TestContext.Current.CancellationToken);

        result.Should().BeOfType<NotFoundResult>();
        await _gdpr.DidNotReceiveWithAnyArgs().ExportForUserAsync(default);
    }

    [HumansFact]
    public async Task DownloadData_ExportFailure_ShowsErrorAndReturnsToPrivacy()
    {
        _gdpr.ExportForUserAsync(_userId, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<GdprExport>(new InvalidOperationException("contributor failed")));
        var ctrl = Create();

        var result = await ctrl.DownloadData(TestContext.Current.CancellationToken);

        var redirect = result.Should().BeOfType<RedirectToActionResult>().Subject;
        redirect.ActionName.Should().Be("Index");
        redirect.ControllerName.Should().Be("MyData");
        ctrl.TempData[TempDataKeys.ErrorMessage].Should().Be("Error_TryAgainLater");
    }

    [HumansFact]
    public async Task DownloadData_CancelledRequest_PropagatesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        _gdpr.ExportForUserAsync(_userId, cancellation.Token)
            .Returns(Task.FromException<GdprExport>(new OperationCanceledException(cancellation.Token)));
        var ctrl = Create();

        var act = () => ctrl.DownloadData(cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [HumansFact]
    public async Task RequestDeletion_success_flashes_the_effective_date_and_returns_to_privacy()
    {
        _deletion.RequestDeletionAsync(_userId, Arg.Any<CancellationToken>())
            .Returns(new DeletionRequestResult(true, EffectiveDeletionDate: Instant.FromUtc(2026, 11, 2, 0, 0)));
        var ctrl = Create();

        var result = await ctrl.RequestDeletion();

        result.Should().BeOfType<RedirectToActionResult>().Which.ActionName.Should().Be("Index");
        ctrl.TempData.ContainsKey(TempDataKeys.SuccessMessage).Should().BeTrue();
    }

    [HumansFact]
    public async Task RequestDeletion_AlreadyPending_FlashesTheSpecificError()
    {
        _deletion.RequestDeletionAsync(_userId, Arg.Any<CancellationToken>())
            .Returns(new DeletionRequestResult(false, "AlreadyPending"));
        var ctrl = Create();

        var result = await ctrl.RequestDeletion();

        result.Should().BeOfType<RedirectToActionResult>().Which.ActionName.Should().Be("Index");
        ctrl.TempData[TempDataKeys.ErrorMessage].Should().Be("Gdpr_Deletion_AlreadyPending");
    }

    [HumansFact]
    public async Task RequestDeletion_without_a_signed_in_member_is_not_found_and_requests_nothing()
    {
        var result = await Create(signedIn: false).RequestDeletion();

        result.Should().BeOfType<NotFoundResult>();
        await _deletion.DidNotReceiveWithAnyArgs().RequestDeletionAsync(default);
    }

    [HumansFact]
    public void Every_action_requires_sign_in_and_the_deletion_post_is_antiforgery_protected()
    {
        typeof(MemberDataController).Should().BeDecoratedWith<AuthorizeAttribute>();
        typeof(MemberDataController).GetMethod(nameof(MemberDataController.RequestDeletion))!
            .Should().BeDecoratedWith<ValidateAntiForgeryTokenAttribute>()
            .And.BeDecoratedWith<HttpPostAttribute>();
        typeof(MemberDataController).GetMethod(nameof(MemberDataController.DownloadData))!
            .Should().BeDecoratedWith<HttpGetAttribute>()
            .And.NotBeDecoratedWith<AllowAnonymousAttribute>();
    }
}
