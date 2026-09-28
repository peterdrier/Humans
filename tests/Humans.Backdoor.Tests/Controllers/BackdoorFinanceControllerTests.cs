using System.Security.Claims;
using System.Text.Json;
using AwesomeAssertions;
using Humans.Backdoor.Contracts;
using Humans.Backdoor.Controllers;
using Humans.Backdoor.Filters;
using Humans.Base.Authorization;
using Humans.Base.Helpers;
using Humans.Budget.Contracts;
using Humans.Expenses.Contracts;
using Humans.Finance.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
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

    [HumansFact]
    public void Every_route_hangs_off_the_api_key_filter()
    {
        var filter = typeof(BackdoorFinanceController)
            .GetCustomAttributes(typeof(ServiceFilterAttribute), inherit: false)
            .Cast<ServiceFilterAttribute>()
            .Single();

        filter.ServiceType.Should().Be(typeof(BackdoorApiKeyAuthFilter));
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
        var httpContext = new DefaultHttpContext();
        if (userId is { } id)
            httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, id.ToString())], BackdoorAuthentication.SchemeName));
        _sut.ControllerContext = new ControllerContext { HttpContext = httpContext };
    }

    private void SetFinanceAdmin(bool isFinanceAdmin) =>
        _auth.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), PolicyNames.FinanceAdminOrAdmin)
            .Returns(isFinanceAdmin ? AuthorizationResult.Success() : AuthorizationResult.Failed());

    /// <summary>Stubs the exact call <c>CanViewAsync</c> makes — the named policy, against this
    /// report — rather than any requirement against any resource, so a test asserting 403 actually
    /// proves the controller asked the right question (peterdrier/Humans#1839, m5).</summary>
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

    /// <summary>D3: the list only ever carries push state, which the browser shows finance admins
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

        var json = JsonSerializer.Serialize(result.Should().BeOfType<OkObjectResult>().Subject.Value);
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
        var report = Report(submitterUserId: userId) with { Note = "Van hire, refund to ES7921000813610123456789" };
        _expenses.GetReviewQueueAsync(userId, true, Arg.Any<CancellationToken>()).Returns([report]);
        _expenses.GetHoldedTimelineAsync(report, Arg.Any<CancellationToken>()).Returns(Timeline());

        var result = await _sut.ExpenseReports(null, null, Xunit.TestContext.Current.CancellationToken);

        var json = JsonSerializer.Serialize(result.Should().BeOfType<OkObjectResult>().Subject.Value);
        json.Should().Contain(@"""payeeIbanMasked"":""ES79****789""");
        json.Should().NotContain("ES7921000813610123456789");
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

        var json = JsonSerializer.Serialize(result.Should().BeOfType<OkObjectResult>().Subject.Value);
        json.Should().Contain($@"""id"":""{approved.Id}""");
        json.Should().NotContain($@"""id"":""{submitted.Id}""");
    }

    /// <summary>D3/M1 on the list route (peterdrier/Humans#1839, fixing m17): a coordinator's own
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

        var json = JsonSerializer.Serialize(result.Should().BeOfType<OkObjectResult>().Subject.Value);
        json.Should().Contain(@"""payeeName"":null");
        json.Should().Contain(@"""payeeIbanMasked"":null");
        json.Should().Contain(@"""syncState"":null");
        await _expenses.DidNotReceiveWithAnyArgs().GetHoldedTimelineAsync(default!, default);
    }

    /// <summary>The <c>year</c> query param (peterdrier/Humans#1839, m6) — untested until now.</summary>
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

        var json = JsonSerializer.Serialize(result.Should().BeOfType<OkObjectResult>().Subject.Value);
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

    /// <summary>D3 (peterdrier/Humans#1839, fixing M1): the timeline and masked IBAN are the
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

        var json = JsonSerializer.Serialize(result.Should().BeOfType<OkObjectResult>().Subject.Value);
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

        var json = JsonSerializer.Serialize(result.Should().BeOfType<OkObjectResult>().Subject.Value);
        json.Should().Contain(@"""syncState"":""Pushed""");
        json.Should().Contain(@"""payeeIbanMasked"":""ES79****789""");
        json.Should().Contain(@"""registeredInHolded"":null");
        json.Should().Contain(@"""owedToMember"":null");
    }

    /// <summary>The M1 scenario: a category coordinator's <c>View</c> succeeds on a different
    /// ground, but they are neither the submitter nor a finance admin — so they get none of the
    /// payee name, IBAN, or Holded ids (peterdrier/Humans#1839, fixing M1).</summary>
    [HumansFact]
    public async Task ExpenseReport_CoordinatorNeitherSubmitterNorFinanceAdmin_SeesNoPayeeIbanOrTimeline()
    {
        SetPrincipal(Guid.NewGuid());
        SetFinanceAdmin(false);
        var report = Report();
        _expenses.GetAsync(report.Id, Arg.Any<CancellationToken>()).Returns(report);
        SetCanView(true, report);

        var result = await _sut.ExpenseReport(report.Id, Xunit.TestContext.Current.CancellationToken);

        var json = JsonSerializer.Serialize(result.Should().BeOfType<OkObjectResult>().Subject.Value);
        json.Should().Contain(@"""payeeName"":null");
        json.Should().Contain(@"""payeeIbanMasked"":null");
        json.Should().Contain(@"""holdedContactId"":null");
        json.Should().Contain(@"""syncState"":null");
        json.Should().Contain(@"""registeredInHolded"":null");
        // Nothing to show either half, so the report/timeline call is skipped outright.
        await _expenses.DidNotReceiveWithAnyArgs().GetHoldedTimelineAsync(default!, default);
    }

    /// <summary>The submitter half of M1: they see the payee name (it is their own reimbursement)
    /// but not the finance-admin-only Holded ids.</summary>
    [HumansFact]
    public async Task ExpenseReport_Submitter_SeesPayeeNameButNotFinanceAdminOnlyHoldedIds()
    {
        var userId = Guid.NewGuid();
        SetPrincipal(userId);
        SetFinanceAdmin(false);
        var report = Report(submitterUserId: userId);
        _expenses.GetAsync(report.Id, Arg.Any<CancellationToken>()).Returns(report);
        SetCanView(true, report);
        _expenses.GetHoldedTimelineAsync(report, Arg.Any<CancellationToken>()).Returns(Timeline());

        var result = await _sut.ExpenseReport(report.Id, Xunit.TestContext.Current.CancellationToken);

        var json = JsonSerializer.Serialize(result.Should().BeOfType<OkObjectResult>().Subject.Value);
        json.Should().Contain(@"""payeeName"":""Ana Torres""");
        json.Should().Contain(@"""holdedContactId"":null");
        json.Should().Contain(@"""holdedSupplierAccountNum"":null");
        json.Should().Contain(@"""holdedDocIds"":null");
    }

    /// <summary>The finance-admin half of M1: they see everything, including the payee name and the
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
        };
        _expenses.GetAsync(report.Id, Arg.Any<CancellationToken>()).Returns(report);
        SetCanView(true, report);
        _expenses.GetHoldedTimelineAsync(report, Arg.Any<CancellationToken>()).Returns(Timeline());

        var result = await _sut.ExpenseReport(report.Id, Xunit.TestContext.Current.CancellationToken);

        var json = JsonSerializer.Serialize(result.Should().BeOfType<OkObjectResult>().Subject.Value);
        json.Should().Contain(@"""payeeName"":""Ana Torres""");
        json.Should().Contain(@"""holdedContactId"":""c-ana""");
        json.Should().Contain(@"""holdedSupplierAccountNum"":40000060");
        json.Should().Contain(@"""holdedDocIds"":[""d1""]");
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

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(
            result.Should().BeOfType<OkObjectResult>().Subject.Value));
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

        var json = JsonSerializer.Serialize(result.Should().BeOfType<OkObjectResult>().Subject.Value);
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

        var json = JsonSerializer.Serialize(result.Should().BeOfType<OkObjectResult>().Subject.Value);
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
        // route, so there is nothing to assert against here — peterdrier/Humans#1839, m16).
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

        var json = JsonSerializer.Serialize(result.Should().BeOfType<OkObjectResult>().Subject.Value);
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

        var json = JsonSerializer.Serialize(result.Should().BeOfType<OkObjectResult>().Subject.Value);
        json.Should().Contain(@"""status"":""Idle""");
        json.Should().Contain(@"""creditorBindingCount"":4");
        json.Should().Contain(@"""docNumber"":""F260001""");
    }
}
