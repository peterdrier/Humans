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
        var localizer = Substitute.For<IStringLocalizer<GdprResource>>();
        localizer[Arg.Any<string>()].Returns(ci => new LocalizedString(ci.Arg<string>(), ci.Arg<string>()));
        var claims = state is { } s
            ? [new Claim(RoleChecks.UserStateClaimType, s.ToString())]
            : Array.Empty<Claim>();
        return new UserController(_users, _deletion, localizer).Wire(_userId, claims);
    }

    private void Stub(UserInfo? info) =>
        _users.GetUserInfoAsync(_userId, Arg.Any<CancellationToken>()).Returns(info);

    [HumansTheory]
    [InlineData(UserState.Active)]
    [InlineData(UserState.Bare)]
    [InlineData(UserState.DeletePending)]
    public async Task Status_sends_everyone_outside_the_excluded_states_home(UserState state)
    {
        var result = await Create(state).Status();

        result.Should().BeOfType<RedirectToActionResult>().Which.ControllerName.Should().Be("Home");
    }

    [HumansTheory]
    [InlineData(UserState.Suspended)]
    [InlineData(UserState.AdminSuspended)]
    [InlineData(UserState.Rejected)]
    [InlineData(UserState.Deleted)]
    [InlineData(UserState.Merged)]
    public async Task Status_shows_the_wall_for_each_excluded_state(UserState state)
    {
        Stub(ControllerTestHost.Info(_userId));

        var result = await Create(state).Status();

        result.Should().BeOfType<ViewResult>().Which.Model
            .Should().BeOfType<AccountStatusViewModel>().Which.State.Should().Be(state);
    }

    [HumansFact]
    public async Task Deletion_without_a_pending_request_redirects_home()
    {
        Stub(ControllerTestHost.Info(_userId));

        var result = await Create(UserState.Active).Deletion();

        result.Should().BeOfType<RedirectToActionResult>().Which.ControllerName.Should().Be("Home");
    }

    [HumansFact]
    public async Task Deletion_for_an_unknown_user_is_not_found()
    {
        Stub(null);

        (await Create(UserState.DeletePending).Deletion()).Should().BeOfType<NotFoundResult>();
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Wall_pages_stop_loading_after_request_cancellation(bool deletion)
    {
        using var request = new CancellationTokenSource();
        var now = Instant.FromUtc(2026, 10, 3, 0, 0);
        _users.GetUserInfoAsync(_userId, Arg.Any<CancellationToken>()).Returns(call =>
        {
            call.Arg<CancellationToken>().ThrowIfCancellationRequested();
            return new ValueTask<UserInfo?>(ControllerTestHost.Info(_userId, u =>
            {
                u.DeletionRequestedAt = now;
                u.DeletionScheduledFor = now + Duration.FromDays(30);
            }));
        });
        var wall = Create(UserState.Suspended);
        wall.HttpContext.RequestAborted = request.Token;
        Func<Task<IActionResult>> load = deletion ? wall.Deletion : wall.Status;

        (await load()).Should().BeOfType<ViewResult>();
        await request.CancelAsync();
        await load.Should().ThrowAsync<OperationCanceledException>();
    }

    [HumansTheory]
    [InlineData(null)]
    [InlineData(10)]
    [InlineData(30)]
    [InlineData(60)]
    public async Task Deletion_reports_the_later_of_grace_deadline_and_ticket_hold(int? holdDays)
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
    public async Task CancelDeletion_success_flashes_and_goes_home()
    {
        Stub(ControllerTestHost.Info(_userId));
        _deletion.CancelDeletionAsync(_userId, Arg.Any<CancellationToken>()).Returns(new OnboardingResult(true));
        var ctrl = Create(UserState.DeletePending);

        var result = await ctrl.CancelDeletion();

        result.Should().BeOfType<RedirectToActionResult>().Which.ControllerName.Should().Be("Home");
        ctrl.TempData[TempDataKeys.SuccessMessage].Should().Be("Gdpr_Deletion_Cancelled");
    }

    [HumansFact]
    public async Task CancelDeletion_without_a_pending_request_flashes_an_error_and_stays()
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
    public async Task CancelDeletion_for_an_unknown_user_is_not_found_and_cancels_nothing()
    {
        Stub(null);

        (await Create(UserState.DeletePending).CancelDeletion()).Should().BeOfType<NotFoundResult>();
        await _deletion.DidNotReceiveWithAnyArgs().CancelDeletionAsync(default, default);
    }

    [HumansFact]
    public void Controller_requires_sign_in_and_the_cancel_post_is_antiforgery_protected()
    {
        typeof(UserController).Should().BeDecoratedWith<AuthorizeAttribute>();
        typeof(UserController).GetMethod(nameof(UserController.CancelDeletion))!
            .Should().BeDecoratedWith<ValidateAntiForgeryTokenAttribute>();
        typeof(UserController).GetMethod(nameof(UserController.CancelDeletion))!
            .Should().NotBeDecoratedWith<AllowAnonymousAttribute>();
    }
}
