using System.Security.Claims;
using System.Text.Json;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Mailer;
using Humans.Application.Interfaces.Mailer.Dtos;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Testing;
using Humans.Web.Controllers.Mailer;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using NodaTime;
using NSubstitute;
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
    private readonly IForgottenEmailService _forgotten = Substitute.For<IForgottenEmailService>();
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
            _mlService, _importService, _userService, _prefs, _forgotten, _audit, _userManager);

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
            ForgottenSkipped: 0, AmbiguousSkipped: 0, UnconfirmedSkipped: 0,
            Errors: 0, Elapsed: Duration.Zero);

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
            SkippedForgotten: 0,
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
            SkippedForgotten: 1,
            SkippedAmbiguous: 0,
            SkippedUnconfirmed: 0);

        var freshDecisions = Enumerable
            .Repeat(Decision(SubscriberOutcome.CreateContact), 10)
            .Concat(Enumerable.Repeat(Decision(SubscriberOutcome.AttachVerified), 5))
            .Concat(Enumerable.Repeat(Decision(SubscriberOutcome.AttachVerifiedConfirmOnly), 2))
            .Concat(Enumerable.Repeat(Decision(SubscriberOutcome.ForgottenSkipped), 1))
            .ToList()
            .AsReadOnly();
        var freshPlan = new ImportPlan(freshDecisions, TotalPulled: 18);

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
