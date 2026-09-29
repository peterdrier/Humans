using System.Security.Claims;
using System.Text.Json;
using AwesomeAssertions;
using Humans.Auth.Contracts;
using Humans.Backdoor.Contracts;
using Humans.Backdoor.Controllers;
using Humans.Backdoor.Filters;
using Humans.Backdoor.Services;
using Humans.Base.Authorization;
using Humans.Base.Helpers;
using Humans.Budget.Contracts;
using Humans.Expenses.Contracts;
using Humans.Finance.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NodaTime;
using NSubstitute;

namespace Humans.Backdoor.Tests.Controllers;

/// <summary>
/// The authorization and formatting the machine finance API does on top of
/// <see cref="IExpenseReportServiceRead"/> and <see cref="IHoldedFinanceServiceRead"/>
/// (peterdrier/Humans#1838). Everything below is controller work: the services are substitutes
/// throughout, and <see cref="IAuthorizationService"/> is a substitute too — the actor matrix
/// itself is <c>ExpenseReportAuthorizationHandlerTests</c>' job.
/// </summary>
public class BackdoorFinanceControllerTests
{
    private readonly IExpenseReportServiceRead _expenses = Substitute.For<IExpenseReportServiceRead>();
    private readonly IHoldedFinanceServiceRead _finance = Substitute.For<IHoldedFinanceServiceRead>();
    private readonly IBudgetServiceRead _budget = Substitute.For<IBudgetServiceRead>();
    private readonly IAuthorizationService _auth = Substitute.For<IAuthorizationService>();
    private readonly IUserServiceRead _users = Substitute.For<IUserServiceRead>();
    private readonly BackdoorFinanceController _sut;

    public BackdoorFinanceControllerTests()
    {
        // Sane default so name-resolution helpers never NRE on an unstubbed lookup; individual
        // tests override when the resolved name itself matters.
        _users.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(new Dictionary<Guid, UserInfo>()));
        _sut = new BackdoorFinanceController(_expenses, _finance, _budget, _auth, _users);
    }

    /// <summary>Read-only surface, GET only — no <c>[Http{Post,Put,Patch,Delete}]</c> action anywhere
    /// on this controller (peterdrier/Humans#1838's acceptance criteria).</summary>
    [HumansFact]
    public void Every_action_is_HttpGet_only()
    {
        var actions = typeof(BackdoorFinanceController)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.DeclaredOnly)
            .Where(m => typeof(Task<IActionResult>).IsAssignableFrom(m.ReturnType))
            .ToList();

        Type[] writeVerbs = [typeof(HttpPostAttribute), typeof(HttpPutAttribute), typeof(HttpPatchAttribute), typeof(HttpDeleteAttribute)];

        actions.Should().NotBeEmpty();
        actions.All(m => m.GetCustomAttributes(typeof(HttpGetAttribute), inherit: false).Length == 1)
            .Should().BeTrue("every action must be [HttpGet]");
        actions.SelectMany(m => m.GetCustomAttributes(inherit: false).Select(a => a.GetType()))
            .Any(writeVerbs.Contains)
            .Should().BeFalse("no action may carry a write-verb attribute");
    }

    private void SetPrincipal(Guid? userId)
    {
        var httpContext = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddSingleton(Options.Create(new JsonOptions())).BuildServiceProvider(),
        };
        if (userId is { } id)
            httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, id.ToString())], BackdoorAuthentication.SchemeName));
        _sut.ControllerContext = new ControllerContext { HttpContext = httpContext };
    }

    /// <summary>What the caller actually receives: the action's result after the controller's own
    /// result filter — the IBAN scrub — has run over it.</summary>
    private string Emitted(IActionResult result)
    {
        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        RunResultFilter(ok);
        return JsonSerializer.Serialize(ok.Value);
    }

    private void RunResultFilter(IActionResult result) =>
        _sut.OnResultExecuting(new ResultExecutingContext(
            new ActionContext(_sut.HttpContext, new RouteData(), new ActionDescriptor()), [], result, _sut));

    private void SetFinanceAdmin(bool isFinanceAdmin) =>
        _auth.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), PolicyNames.FinanceAdminOrAdmin)
            .Returns(isFinanceAdmin ? AuthorizationResult.Success() : AuthorizationResult.Failed());

    /// <summary>Stubs the exact call <c>CanViewAsync</c> makes — the named policy, against this
    /// report — rather than any requirement against any resource, so a test asserting 403 actually
    /// proves the controller asked the right question (peterdrier/Humans#1839).</summary>
    private void SetCanView(bool canView, ExpenseReportDto report) =>
        _auth.AuthorizeAsync(
                Arg.Any<ClaimsPrincipal>(), Arg.Is<object?>(o => ReferenceEquals(o, report)),
                PolicyNames.ExpenseReportView)
            .Returns(canView ? AuthorizationResult.Success() : AuthorizationResult.Failed());

    private static ExpenseReportDto Report(
        Guid? id = null, Guid? submitterUserId = null, string iban = "ES7921000813610123456789",
        ExpenseReportStatus status = ExpenseReportStatus.Submitted, decimal total = 100m) => new()
        {
            Id = id ?? Guid.NewGuid(),
            SubmitterUserId = submitterUserId ?? Guid.NewGuid(),
            BudgetCategoryId = Guid.NewGuid(),
            BudgetYearId = Guid.NewGuid(),
            Status = status,
            PayeeName = "Ana Torres",
            PayeeIban = iban,
            Total = total,
            SubmittedAt = Instant.FromUtc(2026, 5, 1, 9, 0),
            CreatedAt = Instant.FromUtc(2026, 4, 30, 9, 0),
            UpdatedAt = Instant.FromUtc(2026, 5, 1, 9, 0),
            Lines = [],
        };

    private static ExpenseHoldedTimeline Timeline() => new()
    {
        RegisteredInHolded = true,
        OwedToMember = 100m,
        MemberRegisteredTotal = 100m,
        OtherAmount = 0m,
        Paid = false,
        TotalPaid = 0m,
        SyncState = ExpenseHoldedSyncState.Pushed,
    };

    // ─── expense-reports (list) ─────────────────────────────────────────────────

    [HumansFact]
    public async Task ExpenseReports_NoKey_IsUnauthorized()
    {
        SetPrincipal(null);

        var result = await _sut.ExpenseReports(null, null, Xunit.TestContext.Current.CancellationToken);

        result.Should().BeOfType<UnauthorizedResult>();
        await _expenses.DidNotReceiveWithAnyArgs().GetReviewQueueAsync(default, default, default);
    }

    [HumansFact]
    public async Task ExpenseReports_NonFinanceAdmin_SeesOnlyTheirOwnReviewQueue()
    {
        var userId = Guid.NewGuid();
        SetPrincipal(userId);
        SetFinanceAdmin(false);
        _expenses.GetReviewQueueAsync(userId, false, Arg.Any<CancellationToken>()).Returns([Report(submitterUserId: userId)]);

        var result = await _sut.ExpenseReports(null, null, Xunit.TestContext.Current.CancellationToken);

        result.Should().BeOfType<OkObjectResult>();
        await _expenses.Received(1).GetReviewQueueAsync(userId, false, Arg.Any<CancellationToken>());
    }

    /// <summary>The list only ever carries push state, which the browser shows finance admins
    /// only — a non-finance-admin submitter's own report shows the masked IBAN (submitter) but no
    /// push fields, and the timeline is never even fetched.</summary>
    [HumansFact]
    public async Task ExpenseReports_NonFinanceAdminSubmitter_SeesNoPushStateAndSkipsTheTimelineFetch()
    {
        var userId = Guid.NewGuid();
        SetPrincipal(userId);
        SetFinanceAdmin(false);
        var report = Report(submitterUserId: userId);
        _expenses.GetReviewQueueAsync(userId, false, Arg.Any<CancellationToken>()).Returns([report]);

        var result = await _sut.ExpenseReports(null, null, Xunit.TestContext.Current.CancellationToken);

        var json = Emitted(result);
        json.Should().Contain(@"""payeeIbanMasked"":""ES79****789""");
        json.Should().Contain(@"""syncState"":null");
        await _expenses.DidNotReceiveWithAnyArgs().GetHoldedTimelineAsync(default!, default);
    }

    [HumansFact]
    public async Task ExpenseReports_FinanceAdmin_MasksTheIbanAndProjectsPushState()
    {
        var userId = Guid.NewGuid();
        SetPrincipal(userId);
        SetFinanceAdmin(true);
        var report = Report(submitterUserId: userId) with { Note = "Van hire, refund to ES79 2100 0813 6101 2345 6789" };
        _expenses.GetReviewQueueAsync(userId, true, Arg.Any<CancellationToken>()).Returns([report]);
        _expenses.GetHoldedTimelineAsync(report, Arg.Any<CancellationToken>()).Returns(Timeline());

        var result = await _sut.ExpenseReports(null, null, Xunit.TestContext.Current.CancellationToken);

        var json = Emitted(result);
        json.Should().Contain(@"""payeeIbanMasked"":""ES79****789""");
        json.Should().NotContain("ES7921000813610123456789");
        json.Should().NotContain("2345 6789");
        json.Should().Contain(@"""syncState"":""Pushed""");
        json.Should().Contain($@"""id"":""{report.Id}""");
        json.Should().Contain(@"""note"":""Van hire, refund to ES79****789""");
    }

    [HumansFact]
    public async Task ExpenseReports_StatusFilter_ExcludesOtherStatuses()
    {
        var userId = Guid.NewGuid();
        SetPrincipal(userId);
        SetFinanceAdmin(true);
        var submitted = Report(status: ExpenseReportStatus.Submitted);
        var approved = Report(status: ExpenseReportStatus.Approved);
        _expenses.GetReviewQueueAsync(userId, true, Arg.Any<CancellationToken>()).Returns([submitted, approved]);

        var result = await _sut.ExpenseReports("", "Approved", Xunit.TestContext.Current.CancellationToken);

        var json = Emitted(result);
        json.Should().Contain($@"""id"":""{approved.Id}""");
        json.Should().NotContain($@"""id"":""{submitted.Id}""");
    }

    /// <summary>On the list route (peterdrier/Humans#1839), a coordinator's own
    /// review queue never includes reports they submitted or a finance admin's, so this exercises
    /// the "neither" row shape the detail test already covers.</summary>
    [HumansFact]
    public async Task ExpenseReports_NonSubmitterNonFinanceAdminRow_SeesNoPayeeIbanOrPush()
    {
        var userId = Guid.NewGuid();
        SetPrincipal(userId);
        SetFinanceAdmin(false);
        var report = Report();
        _expenses.GetReviewQueueAsync(userId, false, Arg.Any<CancellationToken>()).Returns([report]);

        var result = await _sut.ExpenseReports(null, null, Xunit.TestContext.Current.CancellationToken);

        var json = Emitted(result);
        json.Should().Contain(@"""payeeName"":null");
        json.Should().Contain(@"""payeeIbanMasked"":null");
        json.Should().Contain(@"""syncState"":null");
        await _expenses.DidNotReceiveWithAnyArgs().GetHoldedTimelineAsync(default!, default);
    }

    /// <summary>The <c>year</c> query param (peterdrier/Humans#1839).</summary>
    [HumansFact]
    public async Task ExpenseReports_YearFilter_ExcludesOtherYears()
    {
        var userId = Guid.NewGuid();
        SetPrincipal(userId);
        SetFinanceAdmin(true);
        var report2026 = Report();
        var report2025 = Report();
        _expenses.GetReviewQueueAsync(userId, true, Arg.Any<CancellationToken>()).Returns([report2026, report2025]);
        _budget.GetYearByIdAsync(report2026.BudgetYearId).Returns(
            new BudgetYearDetail(report2026.BudgetYearId, "2026", "2026", BudgetYearStatus.Active, false, []));
        _budget.GetYearByIdAsync(report2025.BudgetYearId).Returns(
            new BudgetYearDetail(report2025.BudgetYearId, "2025", "2025", BudgetYearStatus.Closed, false, []));

        var result = await _sut.ExpenseReports("2026", null, Xunit.TestContext.Current.CancellationToken);

        var json = Emitted(result);
        json.Should().Contain($@"""id"":""{report2026.Id}""");
        json.Should().NotContain($@"""id"":""{report2025.Id}""");
    }

    // ─── expense-reports/{id} (detail) ──────────────────────────────────────────

    [HumansFact]
    public async Task ExpenseReport_NoKey_IsUnauthorized()
    {
        SetPrincipal(null);

        var result = await _sut.ExpenseReport(Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        result.Should().BeOfType<UnauthorizedResult>();
    }

    [HumansFact]
    public async Task ExpenseReport_UnknownId_IsNotFound()
    {
        SetPrincipal(Guid.NewGuid());
        _expenses.GetAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((ExpenseReportDto?)null);

        var result = await _sut.ExpenseReport(Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        result.Should().BeOfType<NotFoundResult>();
    }

    [HumansFact]
    public async Task ExpenseReport_OutsideTheKeyOwnersView_IsForbidden()
    {
        SetPrincipal(Guid.NewGuid());
        var report = Report();
        _expenses.GetAsync(report.Id, Arg.Any<CancellationToken>()).Returns(report);
        SetCanView(false, report);

        var result = await _sut.ExpenseReport(report.Id, Xunit.TestContext.Current.CancellationToken);

        result.Should().BeOfType<StatusCodeResult>().Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    /// <summary>The timeline and masked IBAN (peterdrier/Humans#1839) are the
    /// submitter/finance-admin split <c>ExpensesController.Detail</c> uses, not a blanket grant to
    /// anyone whose <c>View</c> check passes.</summary>
    [HumansFact]
    public async Task ExpenseReport_Submitter_SeesThePaymentHalfNotThePushHalf()
    {
        var userId = Guid.NewGuid();
        SetPrincipal(userId);
        SetFinanceAdmin(false);
        var report = Report(submitterUserId: userId) with
        {
            Note = "Taxi to the site",
            Lines =
            [
                new ExpenseLineDto
                {
                    Id = Guid.NewGuid(), ExpenseReportId = Guid.NewGuid(), Description = "Taxi",
                    Amount = 20m, LineType = ExpenseLineType.Receipt, SortOrder = 0,
                },
            ],
        };
        _expenses.GetAsync(report.Id, Arg.Any<CancellationToken>()).Returns(report);
        SetCanView(true, report);
        _expenses.GetHoldedTimelineAsync(report, Arg.Any<CancellationToken>()).Returns(Timeline());

        var result = await _sut.ExpenseReport(report.Id, Xunit.TestContext.Current.CancellationToken);

        var json = Emitted(result);
        json.Should().Contain(@"""registeredInHolded"":true");
        json.Should().Contain(@"""payeeIbanMasked"":""ES79****789""");
        json.Should().Contain(@"""description"":""Taxi""");
        json.Should().Contain(@"""lineType"":""Receipt""");
        json.Should().Contain(@"""syncState"":null");
        json.Should().Contain(@"""note"":""Taxi to the site""");
    }

    [HumansFact]
    public async Task ExpenseReport_FinanceAdminNotSubmitter_SeesThePushHalfNotThePaymentHalf()
    {
        SetPrincipal(Guid.NewGuid());
        SetFinanceAdmin(true);
        var report = Report();
        _expenses.GetAsync(report.Id, Arg.Any<CancellationToken>()).Returns(report);
        SetCanView(true, report);
        _expenses.GetHoldedTimelineAsync(report, Arg.Any<CancellationToken>()).Returns(Timeline());

        var result = await _sut.ExpenseReport(report.Id, Xunit.TestContext.Current.CancellationToken);

        var json = Emitted(result);
        json.Should().Contain(@"""syncState"":""Pushed""");
        json.Should().Contain(@"""payeeIbanMasked"":""ES79****789""");
        json.Should().Contain(@"""registeredInHolded"":null");
        json.Should().Contain(@"""owedToMember"":null");
    }

    /// <summary>A category coordinator's <c>View</c> succeeds on a different
    /// ground, but they are neither the submitter nor a finance admin — so they get none of the
    /// payee name, IBAN, or Holded ids (peterdrier/Humans#1839).</summary>
    [HumansFact]
    public async Task ExpenseReport_CoordinatorNeitherSubmitterNorFinanceAdmin_SeesNoPayeeIbanOrTimeline()
    {
        SetPrincipal(Guid.NewGuid());
        SetFinanceAdmin(false);
        var report = Report();
        _expenses.GetAsync(report.Id, Arg.Any<CancellationToken>()).Returns(report);
        SetCanView(true, report);

        var result = await _sut.ExpenseReport(report.Id, Xunit.TestContext.Current.CancellationToken);

        var json = Emitted(result);
        json.Should().Contain(@"""payeeName"":null");
        json.Should().Contain(@"""payeeIbanMasked"":null");
        json.Should().Contain(@"""holdedContactId"":null");
        json.Should().Contain(@"""syncState"":null");
        json.Should().Contain(@"""registeredInHolded"":null");
        // Nothing to show either half, so the report/timeline call is skipped outright.
        await _expenses.DidNotReceiveWithAnyArgs().GetHoldedTimelineAsync(default!, default);
    }

    /// <summary>The submitter half: they see the payee name (it is their own reimbursement)
    /// but not the finance-admin-only Holded ids.</summary>
    [HumansFact]
    public async Task ExpenseReport_Submitter_SeesPayeeNameButNotFinanceAdminOnlyHoldedIds()
    {
        var userId = Guid.NewGuid();
        SetPrincipal(userId);
        SetFinanceAdmin(false);
        var report = Report(submitterUserId: userId) with { Lines = [LineWithHoldedDoc("d1")] };
        _expenses.GetAsync(report.Id, Arg.Any<CancellationToken>()).Returns(report);
        SetCanView(true, report);
        _expenses.GetHoldedTimelineAsync(report, Arg.Any<CancellationToken>()).Returns(Timeline());

        var result = await _sut.ExpenseReport(report.Id, Xunit.TestContext.Current.CancellationToken);

        var json = Emitted(result);
        json.Should().Contain(@"""payeeName"":""Ana Torres""");
        json.Should().Contain(@"""holdedContactId"":null");
        json.Should().Contain(@"""holdedSupplierAccountNum"":null");
        json.Should().Contain(@"""holdedDocIds"":null");
        json.Should().Contain(@"""holdedDocId"":null");
    }

    private static ExpenseLineDto LineWithHoldedDoc(string docId) => new()
    {
        Id = Guid.NewGuid(),
        ExpenseReportId = Guid.NewGuid(),
        Description = "Taxi",
        Amount = 20m,
        LineType = ExpenseLineType.Receipt,
        SortOrder = 0,
        HoldedDocId = docId,
    };

    /// <summary>The finance-admin half: they see everything, including the payee name and the
    /// Holded ids the browser's finance card shows only them.</summary>
    [HumansFact]
    public async Task ExpenseReport_FinanceAdmin_SeesPayeeNameAndHoldedIds()
    {
        SetPrincipal(Guid.NewGuid());
        SetFinanceAdmin(true);
        var report = Report() with
        {
            HoldedContactId = "c-ana",
            HoldedSupplierAccountNum = 40000060,
            HoldedDocId = "d1",
            Lines = [LineWithHoldedDoc("d2")],
        };
        _expenses.GetAsync(report.Id, Arg.Any<CancellationToken>()).Returns(report);
        SetCanView(true, report);
        _expenses.GetHoldedTimelineAsync(report, Arg.Any<CancellationToken>()).Returns(Timeline());

        var result = await _sut.ExpenseReport(report.Id, Xunit.TestContext.Current.CancellationToken);

        var json = Emitted(result);
        json.Should().Contain(@"""payeeName"":""Ana Torres""");
        json.Should().Contain(@"""holdedContactId"":""c-ana""");
        json.Should().Contain(@"""holdedSupplierAccountNum"":40000060");
        json.Should().Contain(@"""holdedDocIds"":[""d1""]");
        json.Should().Contain(@"""holdedDocId"":""d2""");
    }

    // ─── attachments ────────────────────────────────────────────────────────────

    [HumansFact]
    public async Task Attachment_NoKey_IsUnauthorized()
    {
        SetPrincipal(null);

        var result = await _sut.Attachment(Guid.NewGuid(), Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        result.Should().BeOfType<UnauthorizedResult>();
    }

    [HumansFact]
    public async Task Attachment_OutsideTheKeyOwnersView_IsForbidden()
    {
        SetPrincipal(Guid.NewGuid());
        var report = Report();
        _expenses.GetReportOwningAttachmentAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(report);
        SetCanView(false, report);

        var result = await _sut.Attachment(report.Id, Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        result.Should().BeOfType<StatusCodeResult>().Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [HumansFact]
    public async Task Attachment_Viewable_ReturnsTheStoredBytes()
    {
        SetPrincipal(Guid.NewGuid());
        var report = Report();
        var attachmentId = Guid.NewGuid();
        _expenses.GetReportOwningAttachmentAsync(attachmentId, Arg.Any<CancellationToken>()).Returns(report);
        SetCanView(true, report);
        _expenses.TryReadAttachmentAsync(report, attachmentId, Arg.Any<CancellationToken>())
            .Returns(new ExpenseAttachmentDownload([1, 2, 3], "application/pdf", "receipt.pdf"));

        var result = await _sut.Attachment(report.Id, attachmentId, Xunit.TestContext.Current.CancellationToken);

        var file = result.Should().BeOfType<FileContentResult>().Subject;
        file.ContentType.Should().Be("application/pdf");
        file.FileDownloadName.Should().Be("receipt.pdf");
        file.FileContents.Should().Equal(1, 2, 3);
    }

    // ─── creditor-accounts ──────────────────────────────────────────────────────

    [HumansFact]
    public async Task CreditorAccounts_NoKey_IsUnauthorized()
    {
        SetPrincipal(null);

        var result = await _sut.CreditorAccounts(Xunit.TestContext.Current.CancellationToken);

        result.Should().BeOfType<UnauthorizedResult>();
    }

    [HumansFact]
    public async Task CreditorAccounts_NonFinanceAdmin_IsForbidden()
    {
        SetPrincipal(Guid.NewGuid());
        SetFinanceAdmin(false);

        var result = await _sut.CreditorAccounts(Xunit.TestContext.Current.CancellationToken);

        result.Should().BeOfType<StatusCodeResult>().Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [HumansFact]
    public async Task CreditorAccounts_ListsBothBindingsOnOneAccountAndTheUnresolvedOnes()
    {
        SetPrincipal(Guid.NewGuid());
        SetFinanceAdmin(true);
        var ana = Guid.NewGuid();
        var ben = Guid.NewGuid();
        var orphan = Guid.NewGuid();
        _finance.ListCreditorAccountsAsync(Arg.Any<CancellationToken>()).Returns((
            (IReadOnlyList<HoldedCreditorAccountRow>)
            [
                new HoldedCreditorAccountRow(40000060, "Ana Torres", -50m, 50m,
                    [
                        new CreditorContactBinding(ana, "c-ana", 40000060, CreditorContactSource.Auto),
                        new CreditorContactBinding(ben, "c-ben", 40000060, CreditorContactSource.Manual),
                    ],
                    "ES79****789"),
            ],
            (IReadOnlyList<CreditorContactBinding>)
            [
                new CreditorContactBinding(orphan, "c-orphan", null, CreditorContactSource.Auto),
            ]));

        var result = await _sut.CreditorAccounts(Xunit.TestContext.Current.CancellationToken);

        using var json = JsonDocument.Parse(Emitted(result));
        var bindings = json.RootElement.GetProperty("accounts")[0].GetProperty("bindings");
        bindings.GetArrayLength().Should().Be(2);
        json.RootElement.GetProperty("unresolved").GetArrayLength().Should().Be(1);
        json.RootElement.GetProperty("unresolved")[0].GetProperty("userId").GetGuid().Should().Be(orphan);
    }

    // ─── creditor-accounts/{num}/ledger ─────────────────────────────────────────

    [HumansFact]
    public async Task CreditorLedger_NoKey_IsUnauthorized()
    {
        SetPrincipal(null);

        var result = await _sut.CreditorLedger(40000060, Xunit.TestContext.Current.CancellationToken);

        result.Should().BeOfType<UnauthorizedResult>();
    }

    [HumansFact]
    public async Task CreditorLedger_NonFinanceAdmin_IsForbidden()
    {
        SetPrincipal(Guid.NewGuid());
        SetFinanceAdmin(false);

        var result = await _sut.CreditorLedger(40000060, Xunit.TestContext.Current.CancellationToken);

        result.Should().BeOfType<StatusCodeResult>().Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [HumansFact]
    public async Task CreditorLedger_MasksTheContactIban()
    {
        SetPrincipal(Guid.NewGuid());
        SetFinanceAdmin(true);
        _finance.GetCreditorLedgerAsync(40000060, Arg.Any<CancellationToken>()).Returns(new HoldedCreditorLedger(
            40000060, -50m, 50m, [],
            new HoldedContactInfo("Ana Torres", null, "ana@example.com", null, null,
                "ES7921000813610123456789", null, null)));

        var result = await _sut.CreditorLedger(40000060, Xunit.TestContext.Current.CancellationToken);

        var json = Emitted(result);
        json.Should().Contain(@"""ibanMasked"":""ES79****789""");
        json.Should().NotContain("ES7921000813610123456789");
    }

    // ─── category-map ───────────────────────────────────────────────────────────

    [HumansFact]
    public async Task CategoryMap_NoKey_IsUnauthorized()
    {
        SetPrincipal(null);

        var result = await _sut.CategoryMap(Xunit.TestContext.Current.CancellationToken);

        result.Should().BeOfType<UnauthorizedResult>();
    }

    [HumansFact]
    public async Task CategoryMap_NonFinanceAdmin_IsForbidden()
    {
        SetPrincipal(Guid.NewGuid());
        SetFinanceAdmin(false);

        var result = await _sut.CategoryMap(Xunit.TestContext.Current.CancellationToken);

        result.Should().BeOfType<StatusCodeResult>().Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [HumansFact]
    public async Task CategoryMap_FinanceAdmin_ProjectsEveryRow()
    {
        SetPrincipal(Guid.NewGuid());
        SetFinanceAdmin(true);
        var categoryId = Guid.NewGuid();
        _finance.GetCategoryMapAsync(Arg.Any<CancellationToken>()).Returns([
            new HoldedCategoryMapRow(categoryId, "Staff", "Operations", 62900101, "acc-1", "staff", true,
                Instant.FromUtc(2026, 6, 1, 0, 0)),
        ]);

        var result = await _sut.CategoryMap(Xunit.TestContext.Current.CancellationToken);

        var json = Emitted(result);
        json.Should().Contain(@"""categoryName"":""Staff""");
        json.Should().Contain(@"""holdedAccountNumber"":62900101");
        json.Should().Contain(@"""isActive"":true");
    }

    // ─── sepa-transfers ─────────────────────────────────────────────────────────

    [HumansFact]
    public async Task SepaTransfers_NoKey_IsUnauthorized()
    {
        SetPrincipal(null);

        var result = await _sut.SepaTransfers(Xunit.TestContext.Current.CancellationToken);

        result.Should().BeOfType<UnauthorizedResult>();
    }

    [HumansFact]
    public async Task SepaTransfers_NonFinanceAdmin_IsForbidden()
    {
        SetPrincipal(Guid.NewGuid());
        SetFinanceAdmin(false);

        var result = await _sut.SepaTransfers(Xunit.TestContext.Current.CancellationToken);

        result.Should().BeOfType<StatusCodeResult>().Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [HumansFact]
    public async Task SepaTransfers_FinanceAdmin_ProjectsMaskedIbanAndBookingState()
    {
        SetPrincipal(Guid.NewGuid());
        SetFinanceAdmin(true);
        var member = Guid.NewGuid();
        var generatedBy = Guid.NewGuid();
        // The row already arrives masked from Finance (Backdoor never sees the raw IBAN for this
        // route, so there is nothing to assert against here — peterdrier/Humans#1839).
        var maskedIban = IbanFormatter.Mask("ES7921000813610123456789");
        _finance.GetSepaTransfersAsync(Arg.Any<CancellationToken>()).Returns((
            (IReadOnlyList<SepaPayoutTransferRow>)
            [
                new SepaPayoutTransferRow(
                    Guid.NewGuid(), Guid.NewGuid(), "batch.xml", Instant.FromUtc(2026, 6, 1, 9, 0),
                    generatedBy, member, 40000060, "c-ana", "Ana Torres", maskedIban, 50m,
                    Instant.FromUtc(2026, 6, 2, 9, 0), generatedBy, "mv-1", null, null, null),
            ],
            (string?)null));

        var result = await _sut.SepaTransfers(Xunit.TestContext.Current.CancellationToken);

        var json = Emitted(result);
        json.Should().Contain($@"""ibanMasked"":""{maskedIban}""");
        json.Should().Contain(@"""holdedBankMovementId"":""mv-1""");
        json.Should().Contain(@"""supplierAccountNum"":40000060");
        json.Should().Contain(@"""reconciledAt"":null");
        json.Should().Contain(@"""reconcilePending"":true");
    }

    // ─── holded-sync ────────────────────────────────────────────────────────────

    [HumansFact]
    public async Task HoldedSync_NoKey_IsUnauthorized()
    {
        SetPrincipal(null);

        var result = await _sut.HoldedSync(Xunit.TestContext.Current.CancellationToken);

        result.Should().BeOfType<UnauthorizedResult>();
    }

    [HumansFact]
    public async Task HoldedSync_NonFinanceAdmin_IsForbidden()
    {
        SetPrincipal(Guid.NewGuid());
        SetFinanceAdmin(false);

        var result = await _sut.HoldedSync(Xunit.TestContext.Current.CancellationToken);

        result.Should().BeOfType<StatusCodeResult>().Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [HumansFact]
    public async Task HoldedSync_FinanceAdmin_ProjectsSyncStateAndUnmatchedDocs()
    {
        SetPrincipal(Guid.NewGuid());
        SetFinanceAdmin(true);
        _finance.GetDocSyncInfoAsync(Arg.Any<CancellationToken>()).Returns(
            new HoldedDocSyncInfo(Instant.FromUtc(2026, 6, 1, 3, 0), "Idle", null, 12, 4));
        _finance.GetUnmatchedAsync(Arg.Any<CancellationToken>()).Returns([
            new HoldedUnmatchedRow("d1", "F260001", "Acme", "Supplies", 30m, "No account, no tag", "https://holded/d1"),
        ]);

        var result = await _sut.HoldedSync(Xunit.TestContext.Current.CancellationToken);

        var json = Emitted(result);
        json.Should().Contain(@"""status"":""Idle""");
        json.Should().Contain(@"""creditorBindingCount"":4");
        json.Should().Contain(@"""docNumber"":""F260001""");
    }

    // ─── IBAN scrub, every route ────────────────────────────────────────────────

    private const string SpacedIban = "es79 2100 0813 6101 2345 6789";
    private const string CompactIban = "ES7921000813610123456789";
    private const string Tainted = "see " + SpacedIban + " or " + CompactIban;

    /// <summary>peterdrier/Humans#1838's guarantee — no response carries an unmasked IBAN — held
    /// for every string field of every route and for the download filename, not per field: an
    /// IBAN planted in each one is masked on the way out (peterdrier/Humans#1839).</summary>
    [HumansFact]
    public async Task Every_route_masks_an_Iban_planted_in_every_string_it_emits()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var userId = Guid.NewGuid();
        SetPrincipal(userId);
        SetFinanceAdmin(true);
        var attachmentId = Guid.NewGuid();
        var report = Report(submitterUserId: userId) with
        {
            Note = Tainted,
            PayeeName = Tainted,
            LastRejectionReason = Tainted,
            HoldedContactId = Tainted,
            HoldedSupplierAccountNum = 40000060,
            HoldedDocId = Tainted,
            Lines =
            [
                LineWithHoldedDoc(Tainted) with
                {
                    Description = Tainted,
                    Attachment = new ExpenseAttachmentDto
                    {
                        Id = attachmentId,
                        OriginalFileName = CompactIban + ".pdf",
                        Extension = ".pdf",
                        ContentType = "application/pdf",
                        SizeBytes = 3,
                        UploadedByUserId = userId,
                        UploadedAt = Instant.FromUtc(2026, 5, 1, 9, 0),
                    },
                },
            ],
        };
        _expenses.GetReviewQueueAsync(userId, true, Arg.Any<CancellationToken>()).Returns([report]);
        _expenses.GetAsync(report.Id, Arg.Any<CancellationToken>()).Returns(report);
        SetCanView(true, report);
        _expenses.GetHoldedTimelineAsync(report, Arg.Any<CancellationToken>())
            .Returns(Timeline() with { LastError = Tainted });
        _expenses.GetReportOwningAttachmentAsync(attachmentId, Arg.Any<CancellationToken>()).Returns(report);
        _expenses.TryReadAttachmentAsync(report, attachmentId, Arg.Any<CancellationToken>())
            .Returns(new ExpenseAttachmentDownload([1, 2, 3], "application/pdf", CompactIban + ".pdf"));
        _finance.ListCreditorAccountsAsync(Arg.Any<CancellationToken>()).Returns((
            (IReadOnlyList<HoldedCreditorAccountRow>)
            [
                new HoldedCreditorAccountRow(40000060, Tainted, -50m, 50m,
                    [new CreditorContactBinding(userId, Tainted, 40000060, CreditorContactSource.Auto)], Tainted),
            ],
            (IReadOnlyList<CreditorContactBinding>)
            [
                new CreditorContactBinding(userId, Tainted, null, CreditorContactSource.Auto),
            ]));
        _finance.GetCreditorLedgerAsync(40000060, Arg.Any<CancellationToken>()).Returns(new HoldedCreditorLedger(
            40000060, -50m, 50m,
            [
                new CreditorLedgerLine
                {
                    EntryNumber = 1,
                    Line = 1,
                    Date = Instant.FromUtc(2026, 5, 1, 9, 0),
                    AccountNum = 40000060,
                    Debit = 0m,
                    Credit = 50m,
                    Type = Tainted,
                    Description = Tainted,
                },
            ],
            new HoldedContactInfo(Tainted, Tainted, Tainted, Tainted, Tainted, CompactIban, Tainted, Tainted)));
        _finance.GetCategoryMapAsync(Arg.Any<CancellationToken>()).Returns([
            new HoldedCategoryMapRow(Guid.NewGuid(), Tainted, Tainted, 62900101, Tainted, Tainted, true,
                Instant.FromUtc(2026, 6, 1, 0, 0)),
        ]);
        _finance.GetSepaTransfersAsync(Arg.Any<CancellationToken>()).Returns((
            (IReadOnlyList<SepaPayoutTransferRow>)
            [
                new SepaPayoutTransferRow(
                    Guid.NewGuid(), Guid.NewGuid(), Tainted, Instant.FromUtc(2026, 6, 1, 9, 0),
                    userId, userId, 40000060, Tainted, Tainted, Tainted, 50m,
                    null, null, Tainted, null, Tainted, Tainted),
            ],
            (string?)Tainted));
        _finance.GetDocSyncInfoAsync(Arg.Any<CancellationToken>()).Returns(
            new HoldedDocSyncInfo(Instant.FromUtc(2026, 6, 1, 3, 0), Tainted, Tainted, 12, 4));
        _finance.GetUnmatchedAsync(Arg.Any<CancellationToken>()).Returns([
            new HoldedUnmatchedRow(Tainted, Tainted, Tainted, Tainted, 30m, Tainted, Tainted),
        ]);

        IActionResult[] results =
        [
            await _sut.ExpenseReports(null, null, ct),
            await _sut.ExpenseReport(report.Id, ct),
            await _sut.CreditorAccounts(ct),
            await _sut.CreditorLedger(40000060, ct),
            await _sut.CategoryMap(ct),
            await _sut.SepaTransfers(ct),
            await _sut.HoldedSync(ct),
        ];
        foreach (var result in results)
        {
            var emitted = Emitted(result);
            emitted.Should().Contain("ES79****789");
            emitted.Should().NotContainEquivalentOf("2100 0813");
            emitted.Should().NotContain("21000813610123456789");
        }

        var file = (await _sut.Attachment(report.Id, attachmentId, ct)).Should().BeOfType<FileContentResult>().Subject;
        RunResultFilter(file);
        file.FileDownloadName.Should().Be("ES79****789.pdf");
        file.FileContents.Should().Equal(1, 2, 3);
    }

    /// <summary>The scrub above is called by hand; this proves MVC itself runs it. A real host,
    /// the real key filter and a real HTTP GET: an IBAN planted in a note comes back masked
    /// (peterdrier/Humans#1839).</summary>
    [HumansFact]
    public async Task Http_get_through_the_mvc_pipeline_masks_an_Iban_planted_in_a_note()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var userId = Guid.NewGuid();
        SetFinanceAdmin(false);
        _expenses.GetReviewQueueAsync(userId, false, Arg.Any<CancellationToken>())
            .Returns([Report(submitterUserId: userId) with { Note = Tainted }]);
        var keys = Substitute.For<IBackdoorApiKeyService>();
        keys.ResolveOwnerAsync("key", Arg.Any<CancellationToken>()).Returns(userId);
        var roles = Substitute.For<IRoleAssignmentService>();
        roles.GetActiveForUserAsync(userId, Arg.Any<CancellationToken>()).Returns([]);

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddControllers()
            .AddApplicationPart(typeof(BackdoorFinanceController).Assembly)
            .ConfigureApplicationPartManager(apm =>
            {
                apm.FeatureProviders.Remove(apm.FeatureProviders.OfType<ControllerFeatureProvider>().Single());
                apm.FeatureProviders.Add(new FinanceControllerOnly());
            });
        builder.Services.AddSingleton(keys).AddSingleton(roles).AddScoped<BackdoorApiKeyAuthFilter>()
            .AddSingleton(_expenses).AddSingleton(_finance).AddSingleton(_budget).AddSingleton(_auth)
            .AddSingleton(_users);
        await using var app = builder.Build();
        app.MapControllers();
        await app.StartAsync(ct);

        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        client.DefaultRequestHeaders.Add(BackdoorApiKeyAuthFilter.ApiKeyHeaderName, "key");
        var body = await client.GetStringAsync("/api/backdoor/finance/expense-reports", ct);
        await app.StopAsync(ct);

        body.Should().Contain(@"""note"":""see es79****789 or ES79****789""");
        body.Should().NotContainEquivalentOf("2100 0813");
        body.Should().NotContain("21000813610123456789");
    }

    private sealed class FinanceControllerOnly : ControllerFeatureProvider
    {
        protected override bool IsController(System.Reflection.TypeInfo typeInfo) =>
            typeInfo.AsType() == typeof(BackdoorFinanceController);
    }
}
