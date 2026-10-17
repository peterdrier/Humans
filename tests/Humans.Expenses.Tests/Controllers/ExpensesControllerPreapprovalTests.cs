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
    private readonly IUserServiceRead _users = Substitute.For<IUserServiceRead>();
    private readonly IExpenseReportService _reports = Substitute.For<IExpenseReportService>();
    private readonly IBudgetServiceRead _budget = Substitute.For<IBudgetServiceRead>();
    private readonly IHoldedFinanceServiceRead _holdedFinance = Substitute.For<IHoldedFinanceServiceRead>();
    private readonly IPreapprovalServiceRead _preapprovals = Substitute.For<IPreapprovalServiceRead>();
    private readonly IStringLocalizer<ExpensesResource> _localizer = Substitute.For<IStringLocalizer<ExpensesResource>>();
    private readonly IAuthorizationService _authorization = Substitute.For<IAuthorizationService>();

    public ExpensesControllerPreapprovalTests()
    {
        _users.GetUserInfoAsync(_actorId, Arg.Any<CancellationToken>()).Returns(
            UserInfo.Create(new User { Id = _actorId }, [], [], [], null, []));
        _reports.GetForSubmitterAsync(_actorId, Arg.Any<CancellationToken>()).Returns([]);
        _reports.GetCoordinatorQueueAsync(_actorId, Arg.Any<CancellationToken>()).Returns([]);
        _budget.GetEffectiveCoordinatorTeamIdsAsync(_actorId).Returns(new HashSet<Guid>());
        _holdedFinance.ListExpenseAccountsAsync(activeOnly: true, Arg.Any<CancellationToken>())
            .Returns([new HoldedExpenseAccountOption(62900001, "acc-1", "Materials", true, true)]);
        _preapprovals.ListClaimableForUserAsync(_actorId, Arg.Any<CancellationToken>()).Returns([]);
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
        return new ExpensesController(_users, _reports, _budget, _holdedFinance, _preapprovals,
            _authorization, TestClock, NullLogger<ExpensesController>.Instance, _localizer)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
            TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>())
        };
    }

    private static GrantOption Grant(string program, LocalDate claimBy, GrantStatus status = GrantStatus.Open) =>
        new(Guid.NewGuid(), program, null, 100m, 62900500, "acc-grant", claimBy, status);

    [HumansFact]
    public async Task Index_ListsOpenGrants_SoonestDeadlineFirst()
    {
        var later = Grant("Alpha", Today.PlusDays(30));
        var soon = Grant("Zulu", Today.PlusDays(3));
        var sameDay = Grant("Bravo", Today.PlusDays(3));
        _preapprovals.ListClaimableForUserAsync(_actorId, Arg.Any<CancellationToken>())
            .Returns([later, soon, sameDay]);

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
        _preapprovals.ListClaimableForUserAsync(_actorId, Arg.Any<CancellationToken>()).Returns([own]);
        _preapprovals.GetGrantAsync(theirs.Id, Arg.Any<CancellationToken>()).Returns(theirs);

        var model = (await Build().New(theirs.Id)).Should().BeOfType<ViewResult>()
            .Which.Model.Should().BeOfType<ExpenseNewViewModel>().Subject;

        model.PreapprovalGrantId.Should().Be(theirs.Id);
        model.Grants.Select(g => g.Id).Should().Equal(theirs.Id, own.Id);
    }

    [HumansFact]
    public async Task NewGet_WithoutAGrant_OffersOnlyTheViewersClaimableGrants()
    {
        var own = Grant("Own", Today.PlusDays(10));
        _preapprovals.ListClaimableForUserAsync(_actorId, Arg.Any<CancellationToken>()).Returns([own]);

        var model = (await Build().New()).Should().BeOfType<ViewResult>()
            .Which.Model.Should().BeOfType<ExpenseNewViewModel>().Subject;

        model.PreapprovalGrantId.Should().BeNull();
        model.Grants.Should().ContainSingle().Which.Id.Should().Be(own.Id);
        await _preapprovals.DidNotReceive().GetGrantAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
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

    [HumansFact]
    public async Task NewPost_WithoutAGrant_StillRequiresTheAccount()
    {
        var controller = Build();
        controller.ModelState.AddModelError(nameof(ExpenseNewViewModel.HoldedAccountNumber), "Validation_Required");

        var result = await controller.New(new ExpenseNewViewModel());

        result.Should().BeOfType<ViewResult>();
        await _reports.DidNotReceiveWithAnyArgs().CreateDraftAsync(default, default, default, default, default, default);
    }

    [HumansFact]
    public async Task NewPost_ShowsTheServicesRefusal_AndKeepsTheGrantSelected()
    {
        var grant = Grant("Own", Today.PlusDays(10));
        _reports.CreateDraftAsync(_actorId, _actorId, 0, null, grant.Id, Arg.Any<CancellationToken>())
            .Returns(new ExpenseCreateDraftResult(false,
                new ExpenseError("Expenses_Validation_PreapprovalNotClaimable", []), null));
        _preapprovals.GetGrantAsync(grant.Id, Arg.Any<CancellationToken>()).Returns(grant);

        var controller = Build();
        var result = await controller.New(new ExpenseNewViewModel { PreapprovalGrantId = grant.Id });

        var model = result.Should().BeOfType<ViewResult>().Which.Model.Should().BeOfType<ExpenseNewViewModel>().Subject;
        model.PreapprovalGrantId.Should().Be(grant.Id);
        model.Grants.Should().ContainSingle().Which.Id.Should().Be(grant.Id);
        controller.TempData[TempDataKeys.ErrorMessage].Should().Be(
            "Expenses_Flash_CreateFailed Expenses_Validation_PreapprovalNotClaimable:");
    }

    [HumansFact]
    public async Task Review_NamesEachPreapprovedReportsGrant_AndFlagsARevokedOne()
    {
        var revoked = Grant("Bus", Today.PlusDays(10), GrantStatus.Revoked);
        var held = Grant("Art", Today.PlusDays(10), GrantStatus.Claimed);
        var onRevoked = ReviewRow(revoked.Id);
        var onHeld = ReviewRow(held.Id);
        var plain = ReviewRow(null);
        _reports.GetReviewQueueAsync(_actorId, false, Arg.Any<CancellationToken>()).Returns([onRevoked, onHeld, plain]);
        _users.GetUserInfosAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, UserInfo>());
        _holdedFinance.ListExpenseAccountsAsync(activeOnly: false, Arg.Any<CancellationToken>()).Returns([]);
        _preapprovals.GetGrantAsync(revoked.Id, Arg.Any<CancellationToken>()).Returns(revoked);
        _preapprovals.GetGrantAsync(held.Id, Arg.Any<CancellationToken>()).Returns(held);

        var model = (await Build().Review()).Should().BeOfType<ViewResult>()
            .Which.Model.Should().BeOfType<ExpenseReviewViewModel>().Subject;

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
