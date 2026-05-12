using System.Security.Claims;
using System.Text.Json;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Mailer;
using Humans.Application.Interfaces.Mailer.Dtos;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Testing;
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
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly ICommunicationPreferenceService _prefs = Substitute.For<ICommunicationPreferenceService>();
    private readonly IAuditLogService _audit = Substitute.For<IAuditLogService>();

    public MailerAdminControllerTests()
    {
        var userStore = Substitute.For<IUserStore<User>>();
        _userManager = Substitute.For<UserManager<User>>(
            userStore, null, null, null, null, null, null, null, null);
    }

    private MailerAdminController BuildSut(ImportPlanCounts? snapshotCounts = null)
    {
        var ctrl = new MailerAdminController(
            _mlService, _importService, _userService, _prefs, _audit,
            NullLogger<MailerAdminController>.Instance, _userManager);

        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()) },
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
        new(TotalPulled: 10, ContactsCreated: 2, PrefsFlipped: 3,
            PrefsPreservedByConflict: 0, UnverifiedRowsDeletedAndSuperseded: 0,
            AmbiguousSkipped: 0, UnconfirmedSkipped: 0,
            VanishedBetweenPlanAndApply: 0, Errors: 0, Elapsed: Duration.Zero);

    // -----------------------------------------------------------------------
    // Commit — drift detected (>10%), redirects back to Import with banner.
    // -----------------------------------------------------------------------

    [HumansFact]
    public async Task Commit_RedirectsToPreview_WhenCountsDriftedMoreThan10Percent()
    {
        // Snapshot from the previous GET /Import had 10 contacts-to-create.
        var snapshot = new ImportPlanCounts(
            WillCreateContact: 10,
            WillAttachWithFlip: 0,
            WillAttachConfirmOnly: 0,
            WillKeepHumansState: 0,
            WillDeleteUnverifiedAndCreate: 0,
            SkippedAmbiguous: 0,
            SkippedUnconfirmed: 0);

        // Fresh plan has 8 CreateContact — a 20% decrease, above the 10% threshold.
        var freshDecisions = Enumerable
            .Repeat(Decision(SubscriberOutcome.CreateContact), 8)
            .ToList()
            .AsReadOnly();
        var freshPlan = new ImportPlan(freshDecisions, TotalPulled: 8);

        _importService.BuildPlanAsync(Arg.Any<CancellationToken>()).Returns(freshPlan);

        var ctrl = BuildSut(snapshot);

        var result = await ctrl.Commit(CancellationToken.None);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(MailerAdminController.Import), redirect.ActionName);
        Assert.Equal(
            "Plan changed since preview — review and re-confirm.",
            ctrl.TempData["Banner"]);
        await _importService.DidNotReceive().ApplyAsync(Arg.Any<ImportPlan>(), Arg.Any<CancellationToken>());
    }

    // -----------------------------------------------------------------------
    // Index — drift: HumansOptedOutMlActive counted correctly.
    // -----------------------------------------------------------------------

    [HumansFact]
    public async Task Drift_CountsHumansOptedOutButMlActive()
    {
        // Arrange: one AttachVerified decision for user A, ML status "active".
        var userId = Guid.NewGuid();
        var decision = new SubscriberDecision(
            Email: "a@example.com",
            Status: "active",
            Outcome: SubscriberOutcome.AttachVerified,
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
            .Returns((IReadOnlyList<MailerLiteGroup>)Array.Empty<MailerLiteGroup>());
        _userService.GetCountByContactSourceAsync(Arg.Any<ContactSource>(), Arg.Any<CancellationToken>())
            .Returns(0);
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
            .Returns((IReadOnlyList<AuditLogEntry>)Array.Empty<AuditLogEntry>());

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

        // Humans-side dependencies still succeed.
        _userService.GetCountByContactSourceAsync(Arg.Any<ContactSource>(), Arg.Any<CancellationToken>())
            .Returns(7);
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
            .Returns((IReadOnlyList<AuditLogEntry>)Array.Empty<AuditLogEntry>());

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
    // Commit — counts within tolerance, calls ApplyAsync and redirects to Index.
    // -----------------------------------------------------------------------

    [HumansFact]
    public async Task Commit_ExecutesApply_WhenCountsWithinTolerance()
    {
        // Snapshot exactly matches the fresh plan counts — zero drift.
        var snapshot = new ImportPlanCounts(
            WillCreateContact: 10,
            WillAttachWithFlip: 5,
            WillAttachConfirmOnly: 2,
            WillKeepHumansState: 0,
            WillDeleteUnverifiedAndCreate: 0,
            SkippedAmbiguous: 0,
            SkippedUnconfirmed: 0);

        var freshDecisions = Enumerable
            .Repeat(Decision(SubscriberOutcome.CreateContact), 10)
            .Concat(Enumerable.Repeat(Decision(SubscriberOutcome.AttachVerified), 5))
            .Concat(Enumerable.Repeat(Decision(SubscriberOutcome.AttachVerifiedConfirmOnly), 2))
            .ToList()
            .AsReadOnly();
        var freshPlan = new ImportPlan(freshDecisions, TotalPulled: 17);

        _importService.BuildPlanAsync(Arg.Any<CancellationToken>()).Returns(freshPlan);
        _importService.ApplyAsync(Arg.Any<ImportPlan>(), Arg.Any<CancellationToken>())
            .Returns(StubResult());

        var ctrl = BuildSut(snapshot);

        var result = await ctrl.Commit(CancellationToken.None);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(MailerAdminController.Index), redirect.ActionName);
        await _importService.Received(1).ApplyAsync(freshPlan, Arg.Any<CancellationToken>());
    }
}
