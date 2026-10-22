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
using NSubstitute;
using Xunit;

namespace Humans.Gdpr.Tests.Controllers;

/// <summary>
/// <see cref="GuestDataController"/>'s deletion request and cancellation for profileless accounts:
/// Gdpr routes, flash mapping, anti-forgery and the signed-in-only gate.
/// </summary>
public sealed class GuestDataControllerTests
{
    private readonly IUserService _users = Substitute.For<IUserService>();
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
        _users.GetUserInfoAsync(_userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(ControllerTestHost.Info(_userId)));
        return new GuestDataController(
                _users, Substitute.For<IGdprService>(), _deletion, Substitute.For<IClock>(),
                NullLogger<GuestDataController>.Instance,
                Substitute.For<IStringLocalizer<SharedResource>>(), _localizer)
            .Wire(signedIn ? _userId : null);
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
    public async Task NoSignedInUser_IsChallenged_AndNothingIsDeleted()
    {
        var ctrl = Sut(signedIn: false);

        (await ctrl.RequestDeletion()).Should().BeOfType<ChallengeResult>();
        (await ctrl.CancelDeletion()).Should().BeOfType<ChallengeResult>();
        _deletion.ReceivedCalls().Should().BeEmpty();
    }
}
