using System.Reflection;
using System.Security.Claims;
using AwesomeAssertions;
using Humans.Base;
using Humans.Base.Authorization;
using Humans.Consent.Controllers;
using Humans.Consent.Services;
using Humans.Onboarding.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Humans.Consent.Tests.Controllers;

/// <summary>
/// The Consent Coordinator review queue.
/// </summary>
/// <remarks>
/// A Volunteer Coordinator may read the queue but not act on it. That is a per-action policy
/// attribute, so a reflection assertion catches an action that forgot it. Each action must report
/// what happened: the queue is the only place a human sees the outcome.
/// </remarks>
public class ConsentReviewQueueControllerTests
{
    private readonly IConsentReviewQueueService _queue = Substitute.For<IConsentReviewQueueService>();
    private readonly IOnboardingIntake _onboarding = Substitute.For<IOnboardingIntake>();
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly IStringLocalizer<ConsentResource> _localizer =
        Substitute.For<IStringLocalizer<ConsentResource>>();
    private readonly IStringLocalizer<SharedResource> _sharedLocalizer =
        Substitute.For<IStringLocalizer<SharedResource>>();

    private readonly Guid _reviewerId = Guid.NewGuid();
    private readonly Guid _subjectId = Guid.NewGuid();

    public ConsentReviewQueueControllerTests()
    {
        _localizer[Arg.Any<string>()].Returns(ci => new LocalizedString(ci.Arg<string>(), ci.Arg<string>()));
        _localizer[Arg.Any<string>(), Arg.Any<object[]>()].Returns(ci =>
            new LocalizedString(ci.Arg<string>(), ci.Arg<string>()));
        _sharedLocalizer[Arg.Any<string>()].Returns(ci => new LocalizedString(ci.Arg<string>(), ci.Arg<string>()));

        _userService.GetUserInfoAsync(_reviewerId, Arg.Any<CancellationToken>())
            .Returns(MakeUserInfo(_reviewerId));
    }

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
        var onTheClass = typeof(ConsentReviewQueueController)
            .GetCustomAttributes<AuthorizeAttribute>()
            .Select(a => a.Policy)
            .ToList();

        onTheClass.Should().Contain(PolicyNames.ReviewQueueAccess,
            "Volunteer Coordinators are meant to read the queue to help new humans");
    }

    /// <summary>
    /// A stale link to an archived id resolves to the survivor. If the page rendered under
    /// the archived id, Clear/Flag/Reject would post that id and the consent check would land
    /// on the anonymized source profile while the coordinator sees the survivor's name.
    /// </summary>
    [HumansFact]
    public async Task Detail_ThroughAMergedAwayId_RedirectsToTheSurvivorsRoute()
    {
        var survivorId = Guid.NewGuid();
        _userService.GetUserInfoAsync(_subjectId, Arg.Any<CancellationToken>())
            .Returns(MakeUserInfo(survivorId));

        var ctrl = BuildSut();
        var result = await ctrl.Detail(_subjectId, TestContext.Current.CancellationToken);

        var redirect = result.Should().BeOfType<RedirectToActionResult>().Which;
        redirect.ActionName.Should().Be(nameof(ConsentReviewQueueController.Detail));
        redirect.RouteValues.Should().ContainKey("userId").WhoseValue.Should().Be(survivorId);
        await _queue.DidNotReceiveWithAnyArgs().GetReviewDetailAsync(default);
    }

    [HumansFact]
    public async Task Clear_TellsTheCoordinatorWhenTheHumanWasAlreadyRejected()
    {
        _queue.ClearAsync(_subjectId, _reviewerId, null, Arg.Any<CancellationToken>())
            .Returns(new OnboardingResult(false, "AlreadyRejected"));

        var ctrl = BuildSut();
        var result = await ctrl.Clear(_subjectId, notes: null);

        result.Should().BeOfType<RedirectToActionResult>()
            .Which.ActionName.Should().Be(nameof(ConsentReviewQueueController.Index));
        ctrl.TempData["ErrorMessage"].Should().Be("Consent_Queue_AlreadyRejected");
    }

    [HumansFact]
    public async Task Clear_SurvivesAThrowingServiceAndSaysSoRatherThan500ing()
    {
        _queue.ClearAsync(_subjectId, _reviewerId, null, Arg.Any<CancellationToken>())
            .Returns<OnboardingResult>(_ => throw new InvalidOperationException("boom"));

        var ctrl = BuildSut();
        var result = await ctrl.Clear(_subjectId, notes: null);

        result.Should().BeOfType<RedirectToActionResult>();
        ctrl.TempData["ErrorMessage"].Should().Be("Common_Error");
    }

    [HumansFact]
    public async Task Flag_RecordsTheReviewerAndTheNotes()
    {
        _queue.FlagAsync(_subjectId, _reviewerId, "needs a chat", Arg.Any<CancellationToken>())
            .Returns(new OnboardingResult(true));

        var ctrl = BuildSut();
        var result = await ctrl.Flag(_subjectId, notes: "needs a chat");

        result.Should().BeOfType<RedirectToActionResult>();
        ctrl.TempData["SuccessMessage"].Should().Be("Consent_Queue_Flagged");
        await _queue.Received(1).FlagAsync(
            _subjectId, _reviewerId, "needs a chat", Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Bulk clear drops selections that are no longer in the queue, so the count it returns
    /// is not the count that was submitted. The coordinator has to be told which of the three
    /// happened — all, some, none — or a half-done sweep reads as a finished one.
    /// </summary>
    [HumansTheory]
    [InlineData(3, 3, "SuccessMessage", "Consent_Queue_BulkCleared")]
    [InlineData(3, 1, "SuccessMessage", "Consent_Queue_BulkClearedPartial")]
    [InlineData(3, 0, "InfoMessage", "Consent_Queue_BulkClearedNone")]
    public async Task BulkClear_DistinguishesAllFromSomeFromNone(
        int selected, int approved, string slot, string expectedKey)
    {
        var ids = Enumerable.Range(0, selected).Select(_ => Guid.NewGuid()).ToList();
        _queue.BulkClearAsync(Arg.Any<IReadOnlyCollection<Guid>>(), _reviewerId, Arg.Any<CancellationToken>())
            .Returns(approved);

        var ctrl = BuildSut();
        await ctrl.BulkClear(ids, TestContext.Current.CancellationToken);

        ctrl.TempData[slot].Should().Be(expectedKey);
    }

    [HumansFact]
    public async Task BulkClear_CancelledRequest_PropagatesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        _queue.BulkClearAsync(Arg.Any<IReadOnlyCollection<Guid>>(), _reviewerId, cancellation.Token)
            .Returns(Task.FromException<int>(new OperationCanceledException(cancellation.Token)));

        var act = () => BuildSut().BulkClear([_subjectId], cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [HumansFact]
    public async Task Reject_WithNoSignedInUser_DoesNotReachTheService()
    {
        var ctrl = BuildSut(signedIn: false);

        var result = await ctrl.Reject(_subjectId, reason: "spam");

        result.Should().BeOfType<NotFoundResult>();
        await _onboarding.DidNotReceiveWithAnyArgs()
            .RejectSignupAsync(default, default, default);
    }

    private static IEnumerable<MethodInfo> MutatingActions() =>
        typeof(ConsentReviewQueueController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.IsDefined(typeof(HttpPostAttribute)));

    private ConsentReviewQueueController BuildSut(bool signedIn = true)
    {
        var ctrl = new ConsentReviewQueueController(
            _userService,
            _queue,
            _onboarding,
            NullLogger<ConsentReviewQueueController>.Instance,
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

    private static UserInfo MakeUserInfo(Guid userId)
    {
        var profile = UserFixtures.Profile(burnerName: "User", isApproved: true);
        return UserInfo.Create(
            new User { Id = userId, PreferredLanguage = "en", BurnerName = profile.BurnerName, State = UserFixtures.StateFor(profile) },
            [], [], [], profile: profile, []);
    }
}
