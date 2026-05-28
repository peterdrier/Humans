using System.Security.Claims;
using System.Text.Json;
using AwesomeAssertions;
using Humans.Application;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Mailer;
using Humans.Application.Interfaces.Mailer.Dtos;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Controllers.Mailer;
using Humans.Web.Models.Mailer;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Humans.Web.Tests.Controllers.Mailer;

/// <summary>
/// Verifies the <see cref="MailerAdminController.Commit"/> action:
/// drift detection against the TempData snapshot redirects back to
/// /Mailer/Admin/Import when counts shifted more than 10%, and calls
/// <see cref="IMailerImportService.ApplyAsync"/> when within tolerance.
/// </summary>
public class MailerAdminControllerTests
{
    private readonly UserManager<User> _userManager;
    private readonly IMailerImportService _importService = Substitute.For<IMailerImportService>();
    private readonly IMailerLiteService _mlService = Substitute.For<IMailerLiteService>();
    private readonly IMailerAudienceSyncService _audienceSync = Substitute.For<IMailerAudienceSyncService>();
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly ICommunicationPreferenceService _prefs = Substitute.For<ICommunicationPreferenceService>();
    private readonly IAuditLogService _audit = Substitute.For<IAuditLogService>();

    public MailerAdminControllerTests()
    {
        var userStore = Substitute.For<IUserStore<User>>();
        _userManager = Substitute.For<UserManager<User>>(
            userStore, null, null, null, null, null, null, null, null);
    }

    private MailerAdminController BuildSut(
        ImportPlanCounts? snapshotCounts = null,
        IEnumerable<IMailerAudience>? audiences = null)
    {
        var ctrl = new MailerAdminController(
            _mlService, _importService, _audienceSync, audiences ?? [],
            _userService, _prefs, _audit,
            NullLogger<MailerAdminController>.Instance);

        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())
                ],
                "test")),
        };
        ctrl.ControllerContext = new ControllerContext { HttpContext = http };
        ctrl.TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>());

        if (snapshotCounts is not null)
        {
            ctrl.TempData["PlanCountsSnapshot"] = JsonSerializer.Serialize(snapshotCounts);
        }

        return ctrl;
    }

    private static SubscriberDecision Decision(SubscriberOutcome outcome) =>
        new("a@b.com", "active", outcome, null, null, null);

    private static ImportResult StubResult() =>
        new(TotalPulled: 10, HumansCreated: 2,
            PrefsFlippedToOptIn: 2, PrefsFlippedToOptOut: 1,
            PrefsKeptByConflict: 0, MarketingFlagsReset: 0,
            UnverifiedEmailsReplaced: 0,
            AmbiguousSkipped: 0, UnconfirmedSkipped: 0,
            VanishedBetweenPlanAndApply: 0, DecisionsThrottled: 0,
            Errors: 0, Elapsed: Duration.Zero);

    // -----------------------------------------------------------------------
    // Commit — drift detected (>10%), redirects back to Import with banner.
    // -----------------------------------------------------------------------

    [HumansFact]
    public async Task Commit_RedirectsToPreview_WhenCountsDriftedMoreThan10Percent()
    {
        // Snapshot from the previous GET /Import had 10 create-new-human decisions.
        var snapshot = new ImportPlanCounts(
            CreateNewHuman: 10,
            ReplaceUnverifiedEmail: 0,
            VerifiedPrefsAlreadyMatch: 0,
            VerifiedFlipToOptIn: 0,
            VerifiedFlipToOptOut: 0,
            VerifiedKeepHumansPref: 0,
            ResetMarketingFlag: 0,
            AmbiguousMultipleVerified: 0,
            UnconfirmedSkipped: 0);

        // Fresh plan has 8 CreateNewHuman — a 20% decrease, above the 10% threshold.
        var freshDecisions = Enumerable
            .Repeat(Decision(SubscriberOutcome.CreateNewHuman), 8)
            .ToList()
            .AsReadOnly();
        var freshPlan = new ImportPlan(freshDecisions, TotalPulled: 8);

        _importService.BuildPlanAsync(Arg.Any<CancellationToken>()).Returns(freshPlan);

        var ctrl = BuildSut(snapshot);

        var result = await ctrl.Commit(maxPerOutcome: null, CancellationToken.None);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(MailerAdminController.Import), redirect.ActionName);
        Assert.Equal(
            "Plan changed since preview — review and re-confirm.",
            ctrl.TempData["Banner"]);
        await _importService.DidNotReceive().ApplyAsync(
            Arg.Any<ImportPlan>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
    }

    // -----------------------------------------------------------------------
    // Index — drift: HumansOptedOutMlActive counted correctly.
    // -----------------------------------------------------------------------

    [HumansFact]
    public async Task Drift_CountsHumansOptedOutButMlActive()
    {
        // Arrange: one verified-match decision for user A, ML status "active".
        var userId = Guid.NewGuid();
        var decision = new SubscriberDecision(
            Email: "a@example.com",
            Status: "active",
            Outcome: SubscriberOutcome.VerifiedFlipToOptIn,
            TargetUserId: userId,
            UnverifiedEmailIdToDelete: null,
            AmbiguousUserIds: null);

        var plan = new ImportPlan(
            Decisions: new[] { decision }.ToList().AsReadOnly(),
            TotalPulled: 1);

        _importService.BuildPlanAsync(Arg.Any<CancellationToken>()).Returns(plan);

        // Prefs: user A is opted out of Marketing.
        _prefs.IsOptedOutAsync(userId, MessageCategory.Marketing, Arg.Any<CancellationToken>())
            .Returns(true);

        // Stub remaining Index dependencies with neutral defaults.
        _mlService.GetAccountSummaryAsync(Arg.Any<CancellationToken>())
            .Returns(new MailerLiteAccountSummary(0, 0, 0, 0, 0));
        _mlService.ListGroupsAsync(Arg.Any<CancellationToken>())
            .Returns([]);
        _userService.GetAllUserInfosAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyCollection<UserInfo>>([]));
        _prefs.GetCountByCategoryAndStateAsync(
                Arg.Any<MessageCategory>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(0);
        _audit.GetFilteredEntriesAsync(
                entityType: Arg.Any<string?>(),
                entityId: Arg.Any<Guid?>(),
                userId: Arg.Any<Guid?>(),
                actions: Arg.Any<IReadOnlyList<AuditAction>?>(),
                limit: Arg.Any<int>(),
                ct: Arg.Any<CancellationToken>())
            .Returns([]);

        var ctrl = BuildSut();

        // Act
        var result = await ctrl.Index(CancellationToken.None);

        // Assert: drift report should count 1 Humans-opted-out / ML-active disagreement.
        var view = Assert.IsType<ViewResult>(result);
        var vm = Assert.IsType<MailerDashboardViewModel>(view.Model);
        Assert.NotNull(vm.Drift);
        Assert.Equal(1, vm.Drift!.HumansOptedOutMlActive);
        Assert.Null(vm.MlError);
    }

    // -----------------------------------------------------------------------
    // Index — MailerLite outage: page still renders with MlError set.
    // -----------------------------------------------------------------------

    [HumansFact]
    public async Task Index_RendersWithMlError_WhenMailerLiteApiUnauthorized()
    {
        // Arrange: ML call throws 401 (e.g., missing/invalid MAILERLITE_API_KEY).
        _mlService.GetAccountSummaryAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException(
                "Response status code does not indicate success: 401 (Unauthorized).",
                inner: null,
                statusCode: System.Net.HttpStatusCode.Unauthorized));

        // Humans-side dependencies still succeed — 7 users sourced from MailerLite.
        _userService.GetAllUserInfosAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyCollection<UserInfo>>(
                Enumerable.Range(0, 7)
                    .Select(_ => MakeUserInfoWithContactSource(ContactSource.MailerLite))
                    .ToList<UserInfo>()));
        _prefs.GetCountByCategoryAndStateAsync(
                Arg.Any<MessageCategory>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(3);
        _audit.GetFilteredEntriesAsync(
                entityType: Arg.Any<string?>(),
                entityId: Arg.Any<Guid?>(),
                userId: Arg.Any<Guid?>(),
                actions: Arg.Any<IReadOnlyList<AuditAction>?>(),
                limit: Arg.Any<int>(),
                ct: Arg.Any<CancellationToken>())
            .Returns([]);

        var ctrl = BuildSut();

        // Act
        var result = await ctrl.Index(CancellationToken.None);

        // Assert: page rendered with friendly error, Humans-side data preserved, no ML data.
        var view = Assert.IsType<ViewResult>(result);
        var vm = Assert.IsType<MailerDashboardViewModel>(view.Model);
        Assert.NotNull(vm.MlError);
        Assert.Contains("401", vm.MlError, StringComparison.Ordinal);
        Assert.Contains("MAILERLITE_API_KEY", vm.MlError, StringComparison.Ordinal);
        Assert.Null(vm.MlSummary);
        Assert.Null(vm.Groups);
        Assert.Null(vm.Drift);
        Assert.Equal(7, vm.HumansMailerLiteContacts);
    }

    // -----------------------------------------------------------------------
    // Refresh — calls IMailerLiteService.RefreshAsync and redirects to Index.
    // -----------------------------------------------------------------------

    [HumansFact]
    public async Task Refresh_CallsRefreshAsync_AndRedirectsToIndex()
    {
        var ctrl = BuildSut();

        var result = await ctrl.Refresh(CancellationToken.None);

        await _mlService.Received(1).RefreshAsync(Arg.Any<CancellationToken>());
        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(MailerAdminController.Index), redirect.ActionName);
        Assert.Equal("MailerLite cache refreshed.", ctrl.TempData["Banner"]);
    }

    [HumansFact]
    public async Task Refresh_OnHttpFailure_SetsErrorBannerAndRedirects()
    {
        _mlService.RefreshAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException(
                "Too Many Requests",
                inner: null,
                statusCode: System.Net.HttpStatusCode.TooManyRequests));

        var ctrl = BuildSut();

        var result = await ctrl.Refresh(CancellationToken.None);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(MailerAdminController.Index), redirect.ActionName);
        var banner = ctrl.TempData["Banner"] as string;
        Assert.NotNull(banner);
        Assert.Contains("Refresh failed", banner, StringComparison.Ordinal);
        Assert.Contains("429", banner, StringComparison.Ordinal);
    }

    [HumansFact]
    public async Task Refresh_OnTimeout_SetsErrorBannerAndRedirects()
    {
        // MailerLiteClient surfaces HttpClient timeouts as TaskCanceledException
        // when the caller's CancellationToken was not the one that fired.
        _mlService.RefreshAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new TaskCanceledException("timed out"));

        var ctrl = BuildSut();

        var result = await ctrl.Refresh(CancellationToken.None);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(MailerAdminController.Index), redirect.ActionName);
        var banner = ctrl.TempData["Banner"] as string;
        Assert.NotNull(banner);
        Assert.Contains("timed out", banner, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // Commit — counts within tolerance, calls ApplyAsync and redirects to Index.
    // -----------------------------------------------------------------------

    [HumansFact]
    public async Task Commit_ExecutesApply_WhenCountsWithinTolerance()
    {
        // Snapshot exactly matches the fresh plan counts — zero drift.
        var snapshot = new ImportPlanCounts(
            CreateNewHuman: 10,
            ReplaceUnverifiedEmail: 0,
            VerifiedPrefsAlreadyMatch: 2,
            VerifiedFlipToOptIn: 5,
            VerifiedFlipToOptOut: 0,
            VerifiedKeepHumansPref: 0,
            ResetMarketingFlag: 0,
            AmbiguousMultipleVerified: 0,
            UnconfirmedSkipped: 0);

        var freshDecisions = Enumerable
            .Repeat(Decision(SubscriberOutcome.CreateNewHuman), 10)
            .Concat(Enumerable.Repeat(Decision(SubscriberOutcome.VerifiedFlipToOptIn), 5))
            .Concat(Enumerable.Repeat(Decision(SubscriberOutcome.VerifiedPrefsAlreadyMatch), 2))
            .ToList()
            .AsReadOnly();
        var freshPlan = new ImportPlan(freshDecisions, TotalPulled: 17);

        _importService.BuildPlanAsync(Arg.Any<CancellationToken>()).Returns(freshPlan);
        _importService.ApplyAsync(Arg.Any<ImportPlan>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(StubResult());

        var ctrl = BuildSut(snapshot);

        var result = await ctrl.Commit(maxPerOutcome: 1, CancellationToken.None);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(MailerAdminController.Index), redirect.ActionName);
        await _importService.Received(1).ApplyAsync(freshPlan, 1, Arg.Any<CancellationToken>());
    }

    // -----------------------------------------------------------------------
    // Debug — renders the per-audience debug VM with the five paged sections.
    // -----------------------------------------------------------------------

    [HumansFact]
    public async Task Debug_RendersFiveSections_AndListsAvailableAudiencesInDropdown()
    {
        var memberId = Guid.NewGuid();
        var audience = Substitute.For<IMailerAudience>();
        audience.Key.Returns("ticket-no-shifts");
        audience.DisplayName.Returns("Ticket holders without a shift");
        audience.MailerLiteGroupName.Returns("Humans - Ticket no Shifts");
        audience.ComputeMemberUserIdsAsync(Arg.Any<CancellationToken>())
            .Returns(new HashSet<Guid> { memberId } as IReadOnlySet<Guid>);

        var other = Substitute.For<IMailerAudience>();
        other.Key.Returns("other-key");
        other.DisplayName.Returns("Other Audience");
        other.MailerLiteGroupName.Returns("Humans - Other");
        other.ComputeMemberUserIdsAsync(Arg.Any<CancellationToken>())
            .Returns(new HashSet<Guid>() as IReadOnlySet<Guid>);

        _mlService.ListGroupsAsync(Arg.Any<CancellationToken>())
            .Returns([
                new MailerLiteGroup("g1", "Humans - Ticket no Shifts", Instant.FromUtc(2026, 1, 1, 0, 0), 0, 0, 0, 0, 0),
            ]);
        _mlService.ListSubscribersAsync(Arg.Any<CancellationToken>())
            .Returns(EmptyAsync());

        _userService.GetAllUserInfosAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyCollection<UserInfo>>(
                [MakeUserInfoWithPrimaryEmail(memberId, "member@example.com")]));

        var ctrl = BuildSut(audiences: [audience, other]);

        var result = await ctrl.Debug(
            "ticket-no-shifts",
            ct: CancellationToken.None);

        var view = Assert.IsType<ViewResult>(result);
        var vm = Assert.IsType<MailerAudienceDebugViewModel>(view.Model);

        vm.SelectedKey.Should().Be("ticket-no-shifts");
        vm.AvailableAudiences.Select(a => a.Key).Should().Equal("ticket-no-shifts", "other-key");
        vm.Expected.Total.Should().Be(1);
        vm.Expected.Rows[0].UserId.Should().Be(memberId);
        vm.Expected.Rows[0].Email.Should().Be("member@example.com");
        vm.ToAdd.Total.Should().Be(1, "member is expected but not yet in ML");
        vm.ToRemove.Total.Should().Be(0);
        vm.CurrentlyInMl.Total.Should().Be(0);
        vm.NonPrimary.Total.Should().Be(0);
        vm.GroupExists.Should().BeTrue();
        vm.Options.PageSizes.Should().Equal(20, 50, 100, 200);
    }

    [HumansFact]
    public async Task Debug_UnknownAudienceKey_Returns404()
    {
        var ctrl = BuildSut(audiences: []);

        var result = await ctrl.Debug("missing", ct: CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    private static UserInfo MakeUserInfoWithPrimaryEmail(Guid userId, string email)
    {
        var now = Instant.FromUtc(2026, 1, 1, 0, 0);
        return UserInfo.Create(
            user: new User
            {
                Id = userId,
                DisplayName = "Member",
                PreferredLanguage = "en",
                CreatedAt = now,
                GoogleEmailStatus = GoogleEmailStatus.Unknown,
            },
            userEmails: [new UserEmail
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Email = email,
                IsVerified = true,
                IsPrimary = true,
                CreatedAt = now,
                UpdatedAt = now,
            }],
            eventParticipations: [],
            externalLogins: [],
            profile: null,
            contactFields: [],
            profileLanguages: [],
            volunteerHistory: [],
            communicationPreferences: []);
    }

    private static async IAsyncEnumerable<MailerLiteSubscriber> EmptyAsync()
    {
        await Task.CompletedTask;
        yield break;
    }

    private static UserInfo MakeUserInfoWithContactSource(ContactSource source)
    {
        var userId = Guid.NewGuid();
        return UserInfo.Create(
            user: new User
            {
                Id = userId,
                DisplayName = "U",
                PreferredLanguage = "en",
                CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
                ContactSource = source,
                GoogleEmailStatus = GoogleEmailStatus.Unknown,
            },
            userEmails: [],
            eventParticipations: [],
            externalLogins: [],
            profile: null,
            contactFields: [],
            profileLanguages: [],
            volunteerHistory: [],
            communicationPreferences: []);
    }
}
