using Humans.Testing;
using Xunit;
using System.Security.Claims;
using AwesomeAssertions;
using Humans.Base.Authorization;
using Humans.Base.Constants;
using Humans.Gdpr.Controllers;
using Humans.Gdpr.Models;
using Humans.Onboarding.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using NodaTime;
using NSubstitute;

namespace Humans.Gdpr.Tests.Controllers;

/// <summary>The account-status wall and pending-deletion landing pages.</summary>
public class UserControllerTests
{
    private readonly Guid _userId = Guid.NewGuid();
    private readonly IUserService _users = Substitute.For<IUserService>();
    private readonly IAccountDeletionService _deletion = Substitute.For<IAccountDeletionService>();

    private UserController Create(UserState? state)
    {
        var localizer = Substitute.For<IStringLocalizer<GdprResource>>().AddFormatting();
        localizer[Arg.Any<string>()].Returns(ci => new LocalizedString(ci.Arg<string>(), ci.Arg<string>()));
        var claims = state is { } s
            ? [new Claim(RoleChecks.UserStateClaimType, s.ToString())]
            : Array.Empty<Claim>();
        return new UserController(_users, _deletion, localizer).Wire(_userId, claims);
    }

    private void Stub(UserInfo? info) =>
        _users.GetUserInfoAsync(_userId, Arg.Any<CancellationToken>()).Returns(info);

    [HumansFact]
    public async Task Deletion_LookupCancelled_ChangesNothing()
    {
        var ctrl = Create(UserState.DeletePending);
        using var request = new CancellationTokenSource();
        ctrl.HttpContext.RequestAborted = request.Token;
        _users.GetUserInfoAsync(_userId, Arg.Any<CancellationToken>()).Returns(call =>
        {
            call.Arg<CancellationToken>().ThrowIfCancellationRequested();
            return new ValueTask<UserInfo?>(ControllerTestHost.Info(_userId));
        });
        await request.CancelAsync();
        Func<Task<IActionResult>> act = () => ctrl.CancelDeletion();

        await act.Should().ThrowAsync<OperationCanceledException>();
        _deletion.ReceivedCalls().Should().BeEmpty();
    }

    [HumansFact]
    public async Task Status_AccountNotWalled_RedirectsHome()
    {
        var result = await Create(UserState.DeletePending).Status();

        result.Should().BeOfType<RedirectToActionResult>().Which.ControllerName.Should().Be("Home");
    }

    [HumansFact]
    public async Task Status_WalledAccount_ShowsWall()
    {
        Stub(ControllerTestHost.Info(_userId));

        var result = await Create(UserState.Merged).Status();

        result.Should().BeOfType<ViewResult>().Which.Model
            .Should().BeOfType<AccountStatusViewModel>().Which.State.Should().Be(UserState.Merged);
    }

    [HumansFact]
    public async Task Deletion_NoPendingRequest_RedirectsHome()
    {
        Stub(ControllerTestHost.Info(_userId));

        var result = await Create(UserState.Active).Deletion();

        result.Should().BeOfType<RedirectToActionResult>().Which.ControllerName.Should().Be("Home");
    }

    [HumansFact]
    public async Task Deletion_UnknownUser_IsNotFound()
    {
        Stub(null);

        (await Create(UserState.DeletePending).Deletion()).Should().BeOfType<NotFoundResult>();
    }

    [HumansTheory]
    [InlineData(null)]
    [InlineData(10)]
    [InlineData(30)]
    [InlineData(60)]
    public async Task Deletion_TicketHoldAfterGrace_ReportsLaterDate(int? holdDays)
    {
        var now = Instant.FromUtc(2026, 10, 3, 0, 0);
        Stub(ControllerTestHost.Info(_userId, u =>
        {
            u.DeletionRequestedAt = now;
            u.DeletionScheduledFor = now + Duration.FromDays(30);
            u.DeletionEligibleAfter = holdDays is { } days ? now + Duration.FromDays(days) : null;
        }));
        var expected = (now + Duration.FromDays(Math.Max(30, holdDays ?? 0))).ToDateTimeUtc();

        var result = await Create(UserState.DeletePending).Deletion();

        result.Should().BeOfType<ViewResult>().Which.Model
            .Should().BeOfType<PendingDeletionViewModel>().Which.ScheduledFor.Should().Be(expected);
    }

    [HumansFact]
    public async Task CancelDeletion_Success_GoesHome()
    {
        Stub(ControllerTestHost.Info(_userId));
        _deletion.CancelDeletionAsync(_userId, Arg.Any<CancellationToken>()).Returns(new OnboardingResult(true));
        var ctrl = Create(UserState.DeletePending);

        var result = await ctrl.CancelDeletion();

        result.Should().BeOfType<RedirectToActionResult>().Which.ControllerName.Should().Be("Home");
        ctrl.TempData[TempDataKeys.SuccessMessage].Should().Be("Gdpr_Deletion_Cancelled");
    }

    [HumansFact]
    public async Task CancelDeletion_NoPendingRequest_ShowsError()
    {
        Stub(ControllerTestHost.Info(_userId));
        _deletion.CancelDeletionAsync(_userId, Arg.Any<CancellationToken>())
            .Returns(new OnboardingResult(false, "NoDeletionPending"));
        var ctrl = Create(UserState.Active);

        var result = await ctrl.CancelDeletion();

        result.Should().BeOfType<RedirectToActionResult>().Which.ActionName.Should().Be("Deletion");
        ctrl.TempData[TempDataKeys.ErrorMessage].Should().Be("Gdpr_Deletion_NotPending");
    }

    [HumansFact]
    public async Task CancelDeletion_UnknownUser_IsNotFound()
    {
        Stub(null);

        (await Create(UserState.DeletePending).CancelDeletion()).Should().BeOfType<NotFoundResult>();
        await _deletion.DidNotReceiveWithAnyArgs().CancelDeletionAsync(default);
    }

    [HumansFact]
    public void Controller_RequiresSignIn()
    {
        typeof(UserController).Should().BeDecoratedWith<AuthorizeAttribute>();
        typeof(UserController).GetMethod(nameof(UserController.CancelDeletion))!
            .Should().BeDecoratedWith<ValidateAntiForgeryTokenAttribute>();
        typeof(UserController).GetMethod(nameof(UserController.CancelDeletion))!
            .Should().NotBeDecoratedWith<AllowAnonymousAttribute>();
    }
}
