using System.Reflection;
using System.Security.Claims;
using AwesomeAssertions;
using Humans.Base;
using Humans.Base.Authorization;
using Humans.Onboarding.Contracts;
using Humans.Onboarding.Controllers;
using Humans.Onboarding.Services;
using Humans.Onboarding.Tests.Services;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Humans.Onboarding.Tests.Controllers;

/// <summary>
/// The Consent Coordinator review queue — the section's only privileged surface, and until
/// this file the only controller in it with no tests at all.
/// </summary>
/// <remarks>
/// Two things are covered here that the section states as invariants and nothing checked.
/// The first is the negative access rule: a Volunteer Coordinator may read the queue and may
/// not act on it, which is a per-action policy attribute and therefore a fact about metadata
/// rather than about behaviour — a reflection assertion is the honest way to test it, and it
/// catches the failure that matters (someone adds an action and forgets the attribute, so it
/// inherits the class-level read policy and every VC can reject signups). The second is that
/// each action tells the coordinator what actually happened: the queue is the only place a
/// human sees the outcome, and a silent failure there reads as success.
/// </remarks>
public class OnboardingReviewControllerTests
{
    private readonly IOnboardingService _onboarding = Substitute.For<IOnboardingService>();
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly IStringLocalizer<OnboardingResource> _localizer =
        Substitute.For<IStringLocalizer<OnboardingResource>>();
    private readonly IStringLocalizer<SharedResource> _sharedLocalizer =
        Substitute.For<IStringLocalizer<SharedResource>>();

    private readonly Guid _reviewerId = Guid.NewGuid();
    private readonly Guid _subjectId = Guid.NewGuid();

    public OnboardingReviewControllerTests()
    {
        _localizer[Arg.Any<string>()].Returns(ci => new LocalizedString(ci.Arg<string>(), ci.Arg<string>()));
        _localizer[Arg.Any<string>(), Arg.Any<object[]>()].Returns(ci =>
            new LocalizedString(ci.Arg<string>(), ci.Arg<string>()));
        _sharedLocalizer[Arg.Any<string>()].Returns(ci => new LocalizedString(ci.Arg<string>(), ci.Arg<string>()));

        _userService.GetUserInfoAsync(_reviewerId, Arg.Any<CancellationToken>())
            .Returns(UserInfoStubs.MakeUserInfo(_reviewerId, displayName: "Reviewer"));
    }

    // --- Negative access rule ---

    /// <summary>
    /// <c>ReviewQueueAccess</c> lets a Volunteer Coordinator see the queue.
    /// Everything that writes must re-gate on <c>ConsentCoordinatorBoardOrAdmin</c>, or the
    /// class-level read policy is all that stands between a VC and a rejected signup.
    /// </summary>
    [HumansFact]
    public void EveryMutatingActionRegatesOnTheConsentCoordinatorPolicy()
    {
        var unguarded = MutatingActions()
            .Where(m => m.GetCustomAttributes<AuthorizeAttribute>()
                .All(a => !string.Equals(a.Policy, PolicyNames.ConsentCoordinatorBoardOrAdmin, StringComparison.Ordinal)))
            .Select(m => m.Name)
            .ToList();

        unguarded.Should().BeEmpty(
            "a Volunteer Coordinator holds ReviewQueueAccess and must not be able to clear, "
            + "flag or reject — the class-level policy alone would let them");
    }

    [HumansFact]
    public void TheQueueItselfIsReadableByEveryReviewQueueRole()
    {
        var onTheClass = typeof(OnboardingReviewController)
            .GetCustomAttributes<AuthorizeAttribute>()
            .Select(a => a.Policy)
            .ToList();

        onTheClass.Should().Contain(PolicyNames.ReviewQueueAccess,
            "Volunteer Coordinators are meant to read the queue to help new humans");
    }

    // --- Outcome reporting ---

    [HumansFact]
    public async Task Clear_TellsTheCoordinatorWhenTheHumanWasAlreadyRejected()
    {
        _onboarding.ClearConsentCheckAsync(_subjectId, _reviewerId, null, Arg.Any<CancellationToken>())
            .Returns(new OnboardingResult(false, "AlreadyRejected"));

        var ctrl = BuildSut();
        var result = await ctrl.Clear(_subjectId, notes: null);

        result.Should().BeOfType<RedirectToActionResult>()
            .Which.ActionName.Should().Be(nameof(OnboardingReviewController.Index));
        ctrl.TempData["ErrorMessage"].Should().Be("Onboarding_ReviewAlreadyRejected");
    }

    [HumansFact]
    public async Task Clear_SurvivesAThrowingServiceAndSaysSoRatherThan500ing()
    {
        _onboarding.ClearConsentCheckAsync(_subjectId, _reviewerId, null, Arg.Any<CancellationToken>())
            .Returns<OnboardingResult>(_ => throw new InvalidOperationException("boom"));

        var ctrl = BuildSut();
        var result = await ctrl.Clear(_subjectId, notes: null);

        result.Should().BeOfType<RedirectToActionResult>();
        ctrl.TempData["ErrorMessage"].Should().Be("Common_Error");
    }

    [HumansFact]
    public async Task Flag_RecordsTheReviewerAndTheNotes()
    {
        _onboarding.FlagConsentCheckAsync(_subjectId, _reviewerId, "needs a chat", Arg.Any<CancellationToken>())
            .Returns(new OnboardingResult(true));

        var ctrl = BuildSut();
        var result = await ctrl.Flag(_subjectId, notes: "needs a chat");

        result.Should().BeOfType<RedirectToActionResult>();
        ctrl.TempData["SuccessMessage"].Should().Be("Onboarding_ReviewFlagged");
        await _onboarding.Received(1).FlagConsentCheckAsync(
            _subjectId, _reviewerId, "needs a chat", Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Bulk clear drops selections that are no longer in the queue, so the count it returns
    /// is not the count that was submitted. The coordinator has to be told which of the three
    /// happened — all, some, none — or a half-done sweep reads as a finished one.
    /// </summary>
    [HumansTheory]
    [InlineData(3, 3, "SuccessMessage", "Onboarding_ReviewBulkCleared")]
    [InlineData(3, 1, "SuccessMessage", "Onboarding_ReviewBulkClearedPartial")]
    [InlineData(3, 0, "InfoMessage", "Onboarding_ReviewBulkClearedNone")]
    public async Task BulkClear_DistinguishesAllFromSomeFromNone(
        int selected, int approved, string slot, string expectedKey)
    {
        var ids = Enumerable.Range(0, selected).Select(_ => Guid.NewGuid()).ToList();
        _onboarding.BulkClearConsentChecksAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(), _reviewerId, Arg.Any<CancellationToken>())
            .Returns(new BulkOnboardingResult(approved));

        var ctrl = BuildSut();
        await ctrl.BulkClear(ids, TestContext.Current.CancellationToken);

        ctrl.TempData[slot].Should().Be(expectedKey);
    }

    [HumansFact]
    public async Task Reject_WithNoSignedInUser_DoesNotReachTheService()
    {
        var ctrl = BuildSut(signedIn: false);

        var result = await ctrl.Reject(_subjectId, reason: "spam");

        result.Should().BeOfType<NotFoundResult>();
        await _onboarding.DidNotReceiveWithAnyArgs()
            .RejectSignupAsync(default, default, default, default);
    }

    private static IEnumerable<MethodInfo> MutatingActions() =>
        typeof(OnboardingReviewController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.IsDefined(typeof(HttpPostAttribute)));

    private OnboardingReviewController BuildSut(bool signedIn = true)
    {
        var ctrl = new OnboardingReviewController(
            _userService,
            _onboarding,
            NullLogger<OnboardingReviewController>.Instance,
            _localizer,
            _sharedLocalizer);

        var identity = signedIn
            ? new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, _reviewerId.ToString())], "test")
            : new ClaimsIdentity();

        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(identity),
        };

        ctrl.ControllerContext = new ControllerContext
        {
            HttpContext = http,
        };
        ctrl.TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>());
        // Pre-set Url so RedirectToAction doesn't resolve IUrlHelperFactory from RequestServices.
        ctrl.Url = Substitute.For<IUrlHelper>();
        return ctrl;
    }
}
