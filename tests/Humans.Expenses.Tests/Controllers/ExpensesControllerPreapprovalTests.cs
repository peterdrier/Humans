using System.Security.Claims;
using AwesomeAssertions;
using Humans.Base.Constants;
using Humans.Budget.Contracts;
using Humans.Expenses.Contracts;
using Humans.Expenses.Controllers;
using Humans.Expenses.Models;
using Humans.Expenses.Services;
using Humans.Finance.Contracts;
using Humans.Preapprovals.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace Humans.Expenses.Tests.Controllers;

/// <summary>How the controller surfaces pre-approvals: the index block, the <c>?grant=</c>
/// preselect on the new form, and a grant standing in for the account at create.</summary>
public sealed class ExpensesControllerPreapprovalTests
{
    private static readonly FakeClock TestClock = new(Instant.FromUtc(2026, 5, 10, 12, 0));
    private static readonly LocalDate Today = new(2026, 5, 10);

    private readonly Guid _actorId = Guid.NewGuid();
    private readonly IUserService _users = Substitute.For<IUserService>();
    private readonly IExpenseReportManagement _reports = Substitute.For<IExpenseReportManagement>();
    private readonly IBudgetService _budget = Substitute.For<IBudgetService>();
    private readonly IFinanceService _finance = Substitute.For<IFinanceService>();
    private readonly IPreapprovalService _preapprovals = Substitute.For<IPreapprovalService>();
    private readonly Dictionary<Guid, GrantOption> _grantStore = new();
    private readonly IStringLocalizer<ExpensesResource> _localizer = Substitute.For<IStringLocalizer<ExpensesResource>>();
    private readonly IAuthorizationService _authorization = Substitute.For<IAuthorizationService>();

    public ExpensesControllerPreapprovalTests()
    {
        _users.GetUserInfoAsync(_actorId, Arg.Any<CancellationToken>()).Returns(
            UserInfo.Create(new User { Id = _actorId }, [], [], null));
        _reports.GetForSubmitterAsync(_actorId, Arg.Any<CancellationToken>()).Returns([]);
        _reports.GetCoordinatorQueueAsync(_actorId, Arg.Any<CancellationToken>()).Returns([]);
        _budget.GetEffectiveCoordinatorTeamIdsAsync(_actorId, Arg.Any<CancellationToken>()).Returns(new HashSet<Guid>());
        _finance.ListExpenseAccountsAsync(activeOnly: true, Arg.Any<CancellationToken>())
            .Returns([new ExpenseAccountOption(62900001, "acc-1", "Materials", true, true)]);
        _preapprovals.GetGrantsAsync(Arg.Any<CancellationToken>())
            .Returns(_ => (IReadOnlyList<GrantOption>)_grantStore.Values.ToList());
        _authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(AuthorizationResult.Failed());
        _localizer[Arg.Any<string>()].Returns(call => new LocalizedString(call.Arg<string>(), call.Arg<string>()));
        _localizer[Arg.Any<string>(), Arg.Any<object[]>()].Returns(call =>
            new LocalizedString(call.Arg<string>(), $"{call.Arg<string>()}:{string.Join(",", call.Arg<object[]>())}"));
    }

    private ExpensesController Build()
    {
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, _actorId.ToString())], "test"))
        };
        return new ExpensesController(_users, _reports, _budget, _finance, _preapprovals,
            _authorization, TestClock, NullLogger<ExpensesController>.Instance, _localizer)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
            TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>())
        };
    }

    public enum ExpenseMutation
    {
        Edit,
        AddLine,
        UpdateLine,
        RemoveLine,
        AttachFile,
        RemoveAttachment,
        Submit,
        Withdraw,
        Iban,
        Endorse,
        CoordinatorReject,
        DeclineLine,
        UndoDeclineLine,
        Approve,
        Reject,
        HoldedRetry,
        New
    }

    public enum NewFormLookup
    {
        ExpenseAccounts,
        ClaimableGrants,
        PreapprovalGrant
    }

    [HumansTheory]
    [Xunit.InlineData(ExpenseMutation.Edit, false)]
    [Xunit.InlineData(ExpenseMutation.Edit, true)]
    [Xunit.InlineData(ExpenseMutation.AddLine, false)]
    [Xunit.InlineData(ExpenseMutation.AddLine, true)]
    [Xunit.InlineData(ExpenseMutation.UpdateLine, false)]
    [Xunit.InlineData(ExpenseMutation.UpdateLine, true)]
    [Xunit.InlineData(ExpenseMutation.RemoveLine, false)]
    [Xunit.InlineData(ExpenseMutation.RemoveLine, true)]
    [Xunit.InlineData(ExpenseMutation.AttachFile, false)]
    [Xunit.InlineData(ExpenseMutation.AttachFile, true)]
    [Xunit.InlineData(ExpenseMutation.RemoveAttachment, false)]
    [Xunit.InlineData(ExpenseMutation.RemoveAttachment, true)]
    [Xunit.InlineData(ExpenseMutation.Submit, false)]
    [Xunit.InlineData(ExpenseMutation.Submit, true)]
    [Xunit.InlineData(ExpenseMutation.Withdraw, false)]
    [Xunit.InlineData(ExpenseMutation.Withdraw, true)]
    [Xunit.InlineData(ExpenseMutation.Iban, false)]
    [Xunit.InlineData(ExpenseMutation.Iban, true)]
    [Xunit.InlineData(ExpenseMutation.Endorse, false)]
    [Xunit.InlineData(ExpenseMutation.Endorse, true)]
    [Xunit.InlineData(ExpenseMutation.CoordinatorReject, false)]
    [Xunit.InlineData(ExpenseMutation.CoordinatorReject, true)]
    [Xunit.InlineData(ExpenseMutation.DeclineLine, false)]
    [Xunit.InlineData(ExpenseMutation.DeclineLine, true)]
    [Xunit.InlineData(ExpenseMutation.UndoDeclineLine, false)]
    [Xunit.InlineData(ExpenseMutation.UndoDeclineLine, true)]
    [Xunit.InlineData(ExpenseMutation.Approve, false)]
    [Xunit.InlineData(ExpenseMutation.Approve, true)]
    [Xunit.InlineData(ExpenseMutation.Reject, false)]
    [Xunit.InlineData(ExpenseMutation.Reject, true)]
    [Xunit.InlineData(ExpenseMutation.HoldedRetry, false)]
    [Xunit.InlineData(ExpenseMutation.HoldedRetry, true)]
    [Xunit.InlineData(ExpenseMutation.New, true)]
    public async Task Mutation_preflight_observes_request_cancellation(ExpenseMutation action, bool actorLookup)
    {
        var report = ReviewRow(null) with { Status = ExpenseReportStatus.Draft };
        using var request = new CancellationTokenSource();
        _users.GetUserInfoAsync(_actorId, Arg.Any<CancellationToken>()).Returns(call =>
        {
            if (actorLookup)
            {
                call.Arg<CancellationToken>().Should().Be(request.Token);
                call.Arg<CancellationToken>().ThrowIfCancellationRequested();
            }
            return new ValueTask<UserInfo?>(UserInfo.Create(new User { Id = _actorId }, [], [], null));
        });
        _reports.GetAsync(report.Id, Arg.Any<CancellationToken>()).Returns(call =>
        {
            if (!actorLookup)
            {
                call.Arg<CancellationToken>().Should().Be(request.Token);
                call.Arg<CancellationToken>().ThrowIfCancellationRequested();
            }
            return report;
        });
        var controller = Build();
        controller.HttpContext.RequestAborted = request.Token;
        await request.CancelAsync();
        var lineId = Guid.NewGuid();
        Func<Task<IActionResult>> invoke = action switch
        {
            ExpenseMutation.Edit => () => controller.Edit(report.Id, new ExpenseEditViewModel()),
            ExpenseMutation.AddLine => () => controller.AddLine(report.Id, new AddLineInputModel(), null),
            ExpenseMutation.UpdateLine => () => controller.UpdateLine(report.Id, new EditLineInputModel()),
            ExpenseMutation.RemoveLine => () => controller.RemoveLine(report.Id, lineId),
            ExpenseMutation.AttachFile => () => controller.AttachFile(report.Id, lineId, null),
            ExpenseMutation.RemoveAttachment => () => controller.RemoveAttachment(report.Id, lineId),
            ExpenseMutation.Submit => () => controller.Submit(report.Id),
            ExpenseMutation.Withdraw => () => controller.Withdraw(report.Id),
            ExpenseMutation.Iban => () => controller.Iban(report.Id, new ExpenseIbanViewModel()),
            ExpenseMutation.Endorse => () => controller.Endorse(report.Id, new EndorseInputModel()),
            ExpenseMutation.CoordinatorReject => () => controller.CoordinatorReject(report.Id, new CoordinatorRejectInputModel()),
            ExpenseMutation.DeclineLine => () => controller.DeclineLine(report.Id, lineId, new DeclineLineInputModel()),
            ExpenseMutation.UndoDeclineLine => () => controller.UndoDeclineLine(report.Id, lineId),
            ExpenseMutation.Approve => () => controller.Approve(report.Id, new ApproveInputModel()),
            ExpenseMutation.Reject => () => controller.Reject(report.Id, new FinanceRejectInputModel()),
            ExpenseMutation.HoldedRetry => () => controller.HoldedRetry(report.Id),
            _ => () => controller.New(new ExpenseNewViewModel()),
        };

        await invoke.Should().ThrowAsync<OperationCanceledException>();
        _reports.ReceivedCalls().Should().OnlyContain(call => call.GetMethodInfo().Name == nameof(IExpenseReportManagement.GetAsync));
        _authorization.ReceivedCalls().Should().BeEmpty();
    }

    [HumansTheory]
    [Xunit.InlineData(NewFormLookup.ExpenseAccounts)]
    [Xunit.InlineData(NewFormLookup.ClaimableGrants)]
    [Xunit.InlineData(NewFormLookup.PreapprovalGrant)]
    public async Task Invalid_new_form_preflight_observes_request_cancellation(NewFormLookup lookup)
    {
        using var request = new CancellationTokenSource();
        var grantId = Guid.NewGuid();
        _finance.ListExpenseAccountsAsync(true, Arg.Any<CancellationToken>()).Returns(call =>
        {
            if (lookup == NewFormLookup.ExpenseAccounts)
            {
                call.Arg<CancellationToken>().Should().Be(request.Token);
                call.Arg<CancellationToken>().ThrowIfCancellationRequested();
            }
            return Array.Empty<ExpenseAccountOption>();
        });
        _preapprovals.GetGrantsAsync(Arg.Any<CancellationToken>()).Returns(call =>
        {
            call.Arg<CancellationToken>().Should().Be(request.Token);
            call.Arg<CancellationToken>().ThrowIfCancellationRequested();
            return Array.Empty<GrantOption>();
        });
        var controller = Build();
        controller.HttpContext.RequestAborted = request.Token;
        controller.ModelState.AddModelError("Note", "invalid");
        await request.CancelAsync();

        var load = () => controller.New(new ExpenseNewViewModel { PreapprovalGrantId = grantId });

        await load.Should().ThrowAsync<OperationCanceledException>();
        _reports.ReceivedCalls().Should().BeEmpty();
        controller.TempData.Should().BeEmpty();
    }

    private static GrantOption Grant(string program, LocalDate claimBy, GrantStatus status = GrantStatus.Open) =>
        new(Guid.NewGuid(), Guid.Empty, [], program, null, 100m, 62900500, "acc-grant", claimBy, status, false);

    private void PutGrant(GrantOption grant) => _grantStore[grant.Id] = grant;

    private void StubClaimable(Guid holder, params GrantOption[] grants)
    {
        foreach (var g in grants) _grantStore[g.Id] = g with { HolderUserId = holder, ClaimableToday = true };
    }

    [HumansFact]
    public async Task EditGet_AccountOptionsObserveRequestCancellation()
    {
        var report = ReviewRow(null) with { Status = ExpenseReportStatus.Draft };
        _reports.GetAsync(report.Id, Arg.Any<CancellationToken>()).Returns(report);
        _authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(),
                Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Success());
        using var request = new CancellationTokenSource();
        await request.CancelAsync();
        _finance.ListExpenseAccountsAsync(activeOnly: true, request.Token)
            .Returns(Task.FromCanceled<IReadOnlyList<ExpenseAccountOption>>(request.Token));
        var controller = Build();
        controller.HttpContext.RequestAborted = request.Token;

        Func<Task> action = () => controller.Edit(report.Id);

        await action.Should().ThrowAsync<OperationCanceledException>();
    }

    [HumansFact]
    public async Task Index_ListsOpenGrants_SoonestDeadlineFirst()
    {
        var later = Grant("Alpha", Today.PlusDays(30));
        var soon = Grant("Zulu", Today.PlusDays(3));
        var sameDay = Grant("Bravo", Today.PlusDays(3));
        StubClaimable(_actorId, later, soon, sameDay);

        var model = (await Build().Index()).Should().BeOfType<ViewResult>()
            .Which.Model.Should().BeOfType<ExpensesIndexViewModel>().Subject;

        model.OpenGrants.Select(g => g.ProgramName).Should().Equal("Bravo", "Zulu", "Alpha");
        model.Today.Should().Be(Today);
    }

    [HumansFact]
    public async Task NewGet_PreselectsTheQueriedGrant_EvenWhenItIsNotTheViewers()
    {
        // A finance admin filing on a member's behalf arrives from /Preapprovals with that member's
        // grant; the form must show it even though it is not in the viewer's own list.
        var own = Grant("Own", Today.PlusDays(10));
        var theirs = Grant("Theirs", Today.PlusDays(5));
        StubClaimable(_actorId, own);
        PutGrant(theirs);

        var model = (await Build().New(theirs.Id)).Should().BeOfType<ViewResult>()
            .Which.Model.Should().BeOfType<ExpenseNewViewModel>().Subject;

        model.PreapprovalGrantId.Should().Be(theirs.Id);
        model.Grants.Select(g => g.Id).Should().Equal(theirs.Id, own.Id);
    }

    [HumansFact]
    public async Task NewGet_WithAGrant_PrefillsTheSubjectFromItsLabel()
    {
        // The grant's label is the project name, which is what the required Subject asks for.
        var labelled = Grant("Art", Today.PlusDays(10)) with { Label = "Giant Duck" };
        StubClaimable(_actorId, labelled);

        var model = (await Build().New(labelled.Id)).Should().BeOfType<ViewResult>()
            .Which.Model.Should().BeOfType<ExpenseNewViewModel>().Subject;

        model.Note.Should().Be("Giant Duck");
    }

    [HumansFact]
    public async Task NewGet_WithoutAGrant_OffersOnlyTheViewersClaimableGrants()
    {
        var own = Grant("Own", Today.PlusDays(10));
        StubClaimable(_actorId, own);
        _finance.ListExpenseAccountsAsync(activeOnly: true, Arg.Any<CancellationToken>()).Returns([
            new ExpenseAccountOption(62900001, "other", "Alpha", false, true),
            new ExpenseAccountOption(62900003, "z", "Zulu", true, true),
            new ExpenseAccountOption(62900005, "a5", "Alpha", true, true),
            new ExpenseAccountOption(62900004, "a4", "alpha", true, true),
        ]);

        var model = (await Build().New()).Should().BeOfType<ViewResult>()
            .Which.Model.Should().BeOfType<ExpenseNewViewModel>().Subject;

        model.PreapprovalGrantId.Should().BeNull();
        model.Grants.Should().ContainSingle().Which.Id.Should().Be(own.Id);
        model.Accounts.Select(a => a.AccountNum).Should().Equal(62900004, 62900005, 62900003, 62900001);
    }

    [HumansFact]
    public async Task NewPost_WithAGrant_CreatesWithoutAnAccount_AndOpensTheDraft()
    {
        var grant = Grant("Own", Today.PlusDays(10));
        var reportId = Guid.NewGuid();
        _reports.CreateDraftAsync(_actorId, _actorId, 0, null, grant.Id, Arg.Any<CancellationToken>())
            .Returns(new ExpenseCreateDraftResult(true, null, reportId));
        var controller = Build();
        // What the form posts with the account select disabled by the picker: no account at all.
        controller.ModelState.AddModelError(nameof(ExpenseNewViewModel.HoldedAccountNumber), "Validation_Required");

        var result = await controller.New(new ExpenseNewViewModel { PreapprovalGrantId = grant.Id });

        result.Should().BeOfType<RedirectToActionResult>().Which.RouteValues!["id"].Should().Be(reportId);
        await _reports.Received(1).CreateDraftAsync(_actorId, _actorId, 0, null, grant.Id, Arg.Any<CancellationToken>());
    }

    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task NewPost_AfterUncertainCreation_DoesNotOfferAnotherCreate(bool unverifiable)
    {
        var reportId = Guid.NewGuid();
        _reports.CreateDraftAsync(_actorId, _actorId, 0, null, null, Arg.Any<CancellationToken>())
            .Returns(new ExpenseCreateDraftResult(false, null, unverifiable ? null : reportId)
            { OutcomeUncertain = unverifiable });
        var controller = Build();

        var result = (await controller.New(new ExpenseNewViewModel())).Should().BeOfType<RedirectToActionResult>().Subject;

        result.ActionName.Should().Be(unverifiable ? nameof(ExpensesController.Index) : nameof(ExpensesController.Edit));
        if (!unverifiable) result.RouteValues!["id"].Should().Be(reportId);
        controller.TempData[TempDataKeys.ErrorMessage].Should().Be(unverifiable
            ? "Expenses_Flash_DraftOutcomeUnknown" : "Expenses_Flash_DraftSavedFollowupFailed");
        await _reports.Received(1).CreateDraftAsync(_actorId, _actorId, 0, null, null, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task NewPost_WithoutAGrant_StillRequiresTheAccount()
    {
        var controller = Build();
        controller.ModelState.AddModelError(nameof(ExpenseNewViewModel.HoldedAccountNumber), "Validation_Required");

        var result = await controller.New(new ExpenseNewViewModel());

        result.Should().BeOfType<ViewResult>();
        await _reports.DidNotReceiveWithAnyArgs().CreateDraftAsync(default, default, default, default);
    }

    [HumansFact]
    public async Task NewPost_ShowsTheServicesRefusal_AndKeepsTheGrantSelected()
    {
        var grant = Grant("Own", Today.PlusDays(10));
        _reports.CreateDraftAsync(_actorId, _actorId, 0, null, grant.Id, Arg.Any<CancellationToken>())
            .Returns(new ExpenseCreateDraftResult(false,
                new ExpenseError("Expenses_Validation_PreapprovalNotClaimable", []), null));
        PutGrant(grant);

        var controller = Build();
        var result = await controller.New(new ExpenseNewViewModel { PreapprovalGrantId = grant.Id });

        var model = result.Should().BeOfType<ViewResult>().Which.Model.Should().BeOfType<ExpenseNewViewModel>().Subject;
        model.PreapprovalGrantId.Should().Be(grant.Id);
        model.Grants.Should().ContainSingle().Which.Id.Should().Be(grant.Id);
        controller.TempData[TempDataKeys.ErrorMessage].Should().Be(
            "Expenses_Flash_CreateFailed Expenses_Validation_PreapprovalNotClaimable:");
    }

    [HumansTheory]
    [Xunit.InlineData(false, false, true)]
    [Xunit.InlineData(true, false, true)]
    [Xunit.InlineData(false, true, true)]
    [Xunit.InlineData(true, true, true)]
    [Xunit.InlineData(false, false, false)]
    [Xunit.InlineData(true, false, false)]
    public async Task LineReview_AuthorizesBeforeDispatch_AndSelectsTheDecisionWindow(
        bool undo, bool financeAccess, bool permitted)
    {
        var report = ReviewRow(null) with { SubmitterUserId = Guid.NewGuid() };
        var lineId = Guid.NewGuid();
        _reports.GetAsync(report.Id, Arg.Any<CancellationToken>()).Returns(report);
        _authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(permitted ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        _authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), FinancePolicies.Manage)
            .Returns(financeAccess ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        _reports.DeclineLineWithResultAsync(report.Id, _actorId, lineId, "Missing invoice", Arg.Any<CancellationToken>())
            .Returns(ExpenseMutationResult.Success);
        _reports.DeclineLineForFinanceReviewWithResultAsync(report.Id, _actorId, lineId, "Missing invoice", Arg.Any<CancellationToken>())
            .Returns(ExpenseMutationResult.Success);
        _reports.UndoDeclineLineWithResultAsync(report.Id, _actorId, lineId, Arg.Any<CancellationToken>())
            .Returns(ExpenseMutationResult.Success);
        _reports.UndoDeclineLineForFinanceReviewWithResultAsync(report.Id, _actorId, lineId, Arg.Any<CancellationToken>())
            .Returns(ExpenseMutationResult.Success);
        var controller = Build();

        var result = undo
            ? await controller.UndoDeclineLine(report.Id, lineId)
            : await controller.DeclineLine(report.Id, lineId, new DeclineLineInputModel { Reason = "Missing invoice" });

        var decisions = _reports.ReceivedCalls().Where(call =>
            call.GetMethodInfo().Name.Contains("DeclineLine", StringComparison.Ordinal)).ToList();
        if (!permitted)
        {
            result.Should().BeOfType<ForbidResult>();
            decisions.Should().BeEmpty();
            return;
        }
        result.Should().BeOfType<RedirectToActionResult>();
        decisions.Should().ContainSingle();
        if (undo && financeAccess)
            await _reports.Received(1).UndoDeclineLineForFinanceReviewWithResultAsync(report.Id, _actorId, lineId, CancellationToken.None);
        else if (undo)
            await _reports.Received(1).UndoDeclineLineWithResultAsync(report.Id, _actorId, lineId, CancellationToken.None);
        else if (financeAccess)
            await _reports.Received(1).DeclineLineForFinanceReviewWithResultAsync(report.Id, _actorId, lineId, "Missing invoice", CancellationToken.None);
        else
            await _reports.Received(1).DeclineLineWithResultAsync(report.Id, _actorId, lineId, "Missing invoice", CancellationToken.None);
    }

    [HumansTheory]
    [Xunit.InlineData("Endorse", false, true)]
    [Xunit.InlineData("Reject", false, true)]
    [Xunit.InlineData("Endorse", true, true)]
    [Xunit.InlineData("Reject", true, true)]
    [Xunit.InlineData("Endorse", false, false)]
    [Xunit.InlineData("Reject", false, false)]
    public async Task FirstReview_AuthorizesBeforeMutation_AndPassesFinanceContextOnlyForAudit(
        string action, bool financeAccess, bool permitted)
    {
        var report = ReviewRow(null) with { SubmitterUserId = Guid.NewGuid() };
        _reports.GetAsync(report.Id, Arg.Any<CancellationToken>()).Returns(report);
        _authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(permitted ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        _authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), FinancePolicies.Manage)
            .Returns(financeAccess ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        _reports.CoordinatorEndorseWithResultAsync(report.Id, _actorId, null, Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns(ExpenseMutationResult.Success);
        _reports.CoordinatorRejectWithResultAsync(report.Id, _actorId, "Missing invoice", Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns(ExpenseMutationResult.Success);
        var controller = Build();

        var result = string.Equals(action, "Endorse", StringComparison.Ordinal)
            ? await controller.Endorse(report.Id, new EndorseInputModel())
            : await controller.CoordinatorReject(report.Id, new CoordinatorRejectInputModel { Reason = "Missing invoice" });

        if (!permitted)
        {
            result.Should().BeOfType<ForbidResult>();
            await _reports.DidNotReceiveWithAnyArgs().CoordinatorEndorseWithResultAsync(default, default, null);
            await _reports.DidNotReceiveWithAnyArgs().CoordinatorRejectWithResultAsync(default, default, "");
            return;
        }
        result.Should().BeOfType<RedirectToActionResult>();
        var label = financeAccess ? "Finance admin" : null;
        if (string.Equals(action, "Endorse", StringComparison.Ordinal))
            await _reports.Received(1).CoordinatorEndorseWithResultAsync(report.Id, _actorId, null, CancellationToken.None, label);
        else
            await _reports.Received(1).CoordinatorRejectWithResultAsync(report.Id, _actorId, "Missing invoice", CancellationToken.None, label);
    }

    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task Review_LoadsQueueWideHoldedFailuresOnlyForFinance(bool financeAccess)
    {
        using var request = new CancellationTokenSource();
        var failedId = Guid.NewGuid();
        _authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), FinancePolicies.Manage)
            .Returns(financeAccess ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        _reports.GetReviewQueueAsync(_actorId, financeAccess, Arg.Any<CancellationToken>()).Returns([]);
        _reports.GetFailedHoldedPushReportIdsAsync(Arg.Any<CancellationToken>()).Returns([failedId]);
        var controller = Build();
        controller.HttpContext.RequestAborted = request.Token;

        var model = (await controller.Review()).Should().BeOfType<ViewResult>()
            .Which.Model.Should().BeOfType<ExpenseReviewViewModel>().Which;

        model.IsFinanceAdmin.Should().Be(financeAccess);
        if (financeAccess)
        {
            model.FailedHoldedPushReportIds.Should().BeEquivalentTo([failedId]);
            await _reports.Received(1).GetFailedHoldedPushReportIdsAsync(request.Token);
        }
        else
        {
            model.FailedHoldedPushReportIds.Should().BeEmpty();
            await _reports.DidNotReceiveWithAnyArgs().GetFailedHoldedPushReportIdsAsync(default);
        }
    }

    [HumansFact]
    public async Task Review_NamesEachPreapprovedReportsGrant_AndFlagsARevokedOne()
    {
        var revoked = Grant("Bus", Today.PlusDays(10), GrantStatus.Revoked);
        var held = Grant("Art", Today.PlusDays(10), GrantStatus.Claimed);
        var now = TestClock.GetCurrentInstant();
        var onRevoked = ReviewRow(revoked.Id) with { SubmittedAt = now.Minus(Duration.FromDays(2)) };
        var onHeld = ReviewRow(held.Id) with { CreatedAt = now.Minus(Duration.FromDays(1)) };
        var plain = ReviewRow(null) with { SubmittedAt = now };
        _reports.GetReviewQueueAsync(_actorId, false, Arg.Any<CancellationToken>()).Returns([onRevoked, onHeld, plain]);
        _users.GetUserInfosAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, UserInfo>());
        _finance.ListExpenseAccountsAsync(activeOnly: false, Arg.Any<CancellationToken>()).Returns([]);
        PutGrant(revoked);
        PutGrant(held);

        var model = (await Build().Review()).Should().BeOfType<ViewResult>()
            .Which.Model.Should().BeOfType<ExpenseReviewViewModel>().Subject;

        model.Reports.Select(r => r.Id).Should().Equal(plain.Id, onHeld.Id, onRevoked.Id);
        model.Grants.Keys.Should().BeEquivalentTo([onRevoked.Id, onHeld.Id]);
        model.IsRevoked(onRevoked.Id).Should().BeTrue();
        model.IsRevoked(onHeld.Id).Should().BeFalse();
        model.IsRevoked(plain.Id).Should().BeFalse();
    }

    [HumansFact]
    public void Banner_ShowsRevoked_AtEveryStatus_AndReadsAsUrgent()
    {
        var revoked = new PreapprovalBannerModel(Grant("Bus", Today.PlusDays(30), GrantStatus.Revoked), Today, ShowDeadline: false);
        var open = new PreapprovalBannerModel(Grant("Bus", Today.PlusDays(30)), Today, ShowDeadline: false);

        revoked.Revoked.Should().BeTrue();
        revoked.Urgent.Should().BeTrue("a cap that is gone must not look like a live pre-approval");
        open.Revoked.Should().BeFalse();
        open.Urgent.Should().BeFalse();
    }

    private ExpenseReportDto ReviewRow(Guid? grantId) => new()
    {
        Id = Guid.NewGuid(),
        SubmitterUserId = _actorId,
        HoldedAccountNumber = 62900500,
        BudgetCategoryId = null,
        BudgetYearId = null,
        PreapprovalGrantId = grantId,
        Status = ExpenseReportStatus.Submitted,
        PayeeName = "Submitter",
        PayeeIban = "",
        Total = 0,
        CreatedAt = default,
        UpdatedAt = default,
        Lines = []
    };
}
