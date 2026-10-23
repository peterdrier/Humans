using AwesomeAssertions;
using Humans.Base;
using Humans.Base.Constants;
using Humans.Gdpr.Contracts;
using Humans.Gdpr.Controllers;
using Humans.Onboarding.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;

namespace Humans.Gdpr.Tests.Controllers;

/// <summary>
/// <see cref="GuestDataController"/>'s export, deletion request and cancellation for profileless accounts:
/// Gdpr routes, flash mapping, anti-forgery and the signed-in-only gate.
/// </summary>
public sealed class GuestDataControllerTests
{
    private readonly IUserService _users = Substitute.For<IUserService>();
    private readonly IGdprService _gdpr = Substitute.For<IGdprService>();
    private readonly IAccountDeletionService _deletion = Substitute.For<IAccountDeletionService>();
    private readonly IStringLocalizer<GdprResource> _localizer = Substitute.For<IStringLocalizer<GdprResource>>();
    private readonly Guid _userId = Guid.NewGuid();

    private GuestDataController Sut(bool signedIn = true)
    {
        _localizer[Arg.Any<string>()].Returns(call =>
        {
            var key = call.ArgAt<string>(0);
            return new LocalizedString(key, key switch
            {
                "Gdpr_Guest_DeletionRequested" => "Your account will be permanently deleted on {0}.",
                _ => key,
            });
        });
        var shared = Substitute.For<IStringLocalizer<SharedResource>>();
        shared[Arg.Any<string>()].Returns(ci => new LocalizedString(ci.Arg<string>(), ci.Arg<string>()));
        _users.GetUserInfoAsync(_userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(ControllerTestHost.Info(_userId)));
        return new GuestDataController(
                _users, _gdpr, _deletion, new FakeClock(Instant.FromUtc(2026, 10, 3, 0, 0)),
                NullLogger<GuestDataController>.Instance, shared, _localizer)
            .Wire(signedIn ? _userId : null);
    }

    [HumansFact]
    public async Task DownloadData_ReturnsTheJsonExportOfTheSignedInAccountOnly()
    {
        _gdpr.ExportForUserAsync(_userId, Arg.Any<CancellationToken>())
            .Returns(new GdprExport("2026-10-03T00:00:00Z", _userId, [], new Dictionary<string, object?>(StringComparer.Ordinal)));

        var result = await Sut().DownloadData(TestContext.Current.CancellationToken);

        var file = result.Should().BeOfType<FileContentResult>().Subject;
        file.ContentType.Should().Be("application/json");
        file.FileDownloadName.Should().Be("nobodies-data-export-2026-10-03.json");
        await _gdpr.Received(1).ExportForUserAsync(_userId, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task DownloadData_ExportFailure_ShowsErrorAndReturnsToTheGuestDashboard()
    {
        _gdpr.ExportForUserAsync(_userId, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<GdprExport>(new InvalidOperationException("contributor failed")));
        var ctrl = Sut();

        var redirect = (await ctrl.DownloadData(TestContext.Current.CancellationToken))
            .Should().BeOfType<RedirectToActionResult>().Subject;

        (redirect.ActionName, redirect.ControllerName).Should().Be(("Index", "Guest"));
        ctrl.TempData[TempDataKeys.ErrorMessage].Should().Be("Error_TryAgainLater");
    }

    [HumansFact]
    public async Task DownloadData_CancelledRequest_PropagatesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        _gdpr.ExportForUserAsync(_userId, cancellation.Token)
            .Returns(Task.FromException<GdprExport>(new OperationCanceledException(cancellation.Token)));
        var ctrl = Sut();

        var act = () => ctrl.DownloadData(cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [HumansTheory]
    [InlineData(nameof(GuestDataController.RequestDeletion), "Gdpr/Guest/RequestDeletion")]
    [InlineData(nameof(GuestDataController.CancelDeletion), "Gdpr/Guest/CancelDeletion")]
    public void DeletionActions_UseGdprGuestUrls_AndRequireAntiForgery(string action, string route)
    {
        var method = typeof(GuestDataController).GetMethod(action)!;
        method.GetCustomAttributes(typeof(HttpPostAttribute), false).Cast<HttpPostAttribute>()
            .Should().ContainSingle().Which.Template.Should().Be(route);
        method.GetCustomAttributes(typeof(ValidateAntiForgeryTokenAttribute), false).Should().ContainSingle();
        method.GetCustomAttributes(typeof(AllowAnonymousAttribute), false).Should().BeEmpty();
        typeof(GuestDataController).GetCustomAttributes(typeof(AuthorizeAttribute), false).Should().ContainSingle();
    }

    [HumansFact]
    public async Task RequestDeletion_Success_RedirectsToTheGuestDashboardWithTheDate()
    {
        _deletion.RequestDeletionAsync(_userId, Arg.Any<CancellationToken>())
            .Returns(new DeletionRequestResult(true, EffectiveDeletionDate: Instant.FromUtc(2026, 6, 15, 0, 0)));
        var ctrl = Sut();

        var redirect = (await ctrl.RequestDeletion()).Should().BeOfType<RedirectToActionResult>().Subject;

        (redirect.ActionName, redirect.ControllerName).Should().Be(("Index", "Guest"));
        ((string)ctrl.TempData[TempDataKeys.SuccessMessage]!).Should().Contain("permanently deleted");
    }

    [HumansFact]
    public async Task RequestDeletion_AlreadyPending_ShowsTheSpecificError()
    {
        _deletion.RequestDeletionAsync(_userId, Arg.Any<CancellationToken>())
            .Returns(new DeletionRequestResult(false, "AlreadyPending"));
        var ctrl = Sut();

        await ctrl.RequestDeletion();

        ctrl.TempData[TempDataKeys.ErrorMessage].Should().Be("Gdpr_Deletion_AlreadyPending");
    }

    [HumansTheory]
    [InlineData("NoDeletionPending", "Gdpr_Deletion_NotPending")]
    [InlineData("Other", "Gdpr_Guest_CancelDeletionFailed")]
    public async Task CancelDeletion_Failure_MapsTheError(string errorKey, string expected)
    {
        _deletion.CancelDeletionAsync(_userId, Arg.Any<CancellationToken>())
            .Returns(new OnboardingResult(false, errorKey));
        var ctrl = Sut();

        await ctrl.CancelDeletion();

        ctrl.TempData[TempDataKeys.ErrorMessage].Should().Be(expected);
    }

    [HumansFact]
    public async Task CancelDeletion_Success_ConfirmsAndRedirects()
    {
        _deletion.CancelDeletionAsync(_userId, Arg.Any<CancellationToken>()).Returns(new OnboardingResult(true));
        var ctrl = Sut();

        var redirect = (await ctrl.CancelDeletion()).Should().BeOfType<RedirectToActionResult>().Subject;

        redirect.ControllerName.Should().Be("Guest");
        ctrl.TempData[TempDataKeys.SuccessMessage].Should().Be("Gdpr_Deletion_Cancelled");
    }

    [HumansFact]
    public async Task NoSignedInUser_IsChallenged_AndNothingIsExportedOrDeleted()
    {
        var ctrl = Sut(signedIn: false);

        (await ctrl.DownloadData(TestContext.Current.CancellationToken)).Should().BeOfType<ChallengeResult>();
        (await ctrl.RequestDeletion()).Should().BeOfType<ChallengeResult>();
        (await ctrl.CancelDeletion()).Should().BeOfType<ChallengeResult>();
        _gdpr.ReceivedCalls().Should().BeEmpty();
        _deletion.ReceivedCalls().Should().BeEmpty();
    }
}
