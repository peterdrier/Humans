using System.Security.Claims;
using AwesomeAssertions;
using Humans.Application.Interfaces.Budget;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Services.Expenses.Dtos;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Authorization.Requirements;
using Microsoft.AspNetCore.Authorization;
using NSubstitute;
using NodaTime;

namespace Humans.Application.Tests.Authorization;

/// <summary>
/// Unit tests for ExpenseReportAuthorizationHandler.
/// Covers the actors-and-roles matrix: submitter / coordinator-of-this-category /
/// coordinator-of-other-category / FinanceAdmin / Admin / random user × key operations.
/// </summary>
public sealed class ExpenseReportAuthorizationHandlerTests
{
    private readonly IBudgetService _budgetService = Substitute.For<IBudgetService>();
    private readonly ITeamService _teamService = Substitute.For<ITeamService>();
    private readonly ExpenseReportAuthorizationHandler _handler;

    private static readonly Guid SubmitterId = Guid.NewGuid();
    private static readonly Guid CoordinatorId = Guid.NewGuid();
    private static readonly Guid OtherCoordinatorId = Guid.NewGuid();
    private static readonly Guid RandomUserId = Guid.NewGuid();
    private static readonly Guid CategoryId = Guid.NewGuid();
    private static readonly Guid TeamId = Guid.NewGuid();
    private static readonly Guid OtherTeamId = Guid.NewGuid();

    public ExpenseReportAuthorizationHandlerTests()
    {
        _handler = new ExpenseReportAuthorizationHandler(_budgetService, _teamService);

        // Category is linked to TeamId
        _budgetService.GetCategoryByIdAsync(CategoryId)
            .Returns(new BudgetCategory { Id = CategoryId, TeamId = TeamId });

        // CoordinatorId is a coordinator of TeamId; OtherCoordinatorId is not
        _teamService.IsUserCoordinatorOfTeamAsync(TeamId, CoordinatorId)
            .Returns(true);
        _teamService.IsUserCoordinatorOfTeamAsync(TeamId, OtherCoordinatorId)
            .Returns(false);
        _teamService.IsUserCoordinatorOfTeamAsync(Arg.Any<Guid>(), RandomUserId)
            .Returns(false);
        _teamService.IsUserCoordinatorOfTeamAsync(Arg.Any<Guid>(), SubmitterId)
            .Returns(false);
    }

    // ─── Submitter × View ────────────────────────────────────────────────────

    [HumansFact]
    public async Task Submitter_CanView_OwnReport()
    {
        var report = MakeReport(SubmitterId, ExpenseReportStatus.Submitted);
        var result = await EvaluateAsync(CreateUser(SubmitterId), report, ExpenseReportOperation.View);
        result.Should().BeTrue();
    }

    // ─── Submitter × Edit ────────────────────────────────────────────────────

    [HumansFact]
    public async Task Submitter_CanEdit_OwnDraftReport()
    {
        var report = MakeReport(SubmitterId, ExpenseReportStatus.Draft);
        var result = await EvaluateAsync(CreateUser(SubmitterId), report, ExpenseReportOperation.Edit);
        result.Should().BeTrue();
    }

    [HumansFact]
    public async Task Submitter_CannotEdit_OwnSubmittedReport()
    {
        // Lines are frozen at submission per docs/sections/Expenses.md invariants
        // and ExpenseReportService.RequireEditableReportAsync enforcement.
        var report = MakeReport(SubmitterId, ExpenseReportStatus.Submitted);
        var result = await EvaluateAsync(CreateUser(SubmitterId), report, ExpenseReportOperation.Edit);
        result.Should().BeFalse();
    }

    [HumansFact]
    public async Task Submitter_CannotEdit_ApprovedReport()
    {
        var report = MakeReport(SubmitterId, ExpenseReportStatus.Approved);
        var result = await EvaluateAsync(CreateUser(SubmitterId), report, ExpenseReportOperation.Edit);
        result.Should().BeFalse();
    }

    // ─── Submitter × Submit ──────────────────────────────────────────────────

    [HumansFact]
    public async Task Submitter_CanSubmit_OwnDraftReport()
    {
        var report = MakeReport(SubmitterId, ExpenseReportStatus.Draft);
        var result = await EvaluateAsync(CreateUser(SubmitterId), report, ExpenseReportOperation.Submit);
        result.Should().BeTrue();
    }

    [HumansFact]
    public async Task Submitter_CannotSubmit_AlreadySubmittedReport()
    {
        var report = MakeReport(SubmitterId, ExpenseReportStatus.Submitted);
        var result = await EvaluateAsync(CreateUser(SubmitterId), report, ExpenseReportOperation.Submit);
        result.Should().BeFalse();
    }

    // ─── Submitter × Withdraw ────────────────────────────────────────────────

    [HumansFact]
    public async Task Submitter_CanWithdraw_SubmittedReport()
    {
        var report = MakeReport(SubmitterId, ExpenseReportStatus.Submitted);
        var result = await EvaluateAsync(CreateUser(SubmitterId), report, ExpenseReportOperation.Withdraw);
        result.Should().BeTrue();
    }

    [HumansFact]
    public async Task Submitter_CanWithdraw_ApprovedReport()
    {
        // Per docs/sections/Expenses.md invariants: terminal alternates include
        // Withdrawn from Approved (before SEPA payout is sent).
        var report = MakeReport(SubmitterId, ExpenseReportStatus.Approved);
        var result = await EvaluateAsync(CreateUser(SubmitterId), report, ExpenseReportOperation.Withdraw);
        result.Should().BeTrue();
    }

    // ─── Submitter cannot approve/endorse/coordinatorreject ──────────────────

    [HumansFact]
    public async Task Submitter_CannotApprove_OwnReport()
    {
        var report = MakeReport(SubmitterId, ExpenseReportStatus.Submitted);
        var result = await EvaluateAsync(CreateUser(SubmitterId), report, ExpenseReportOperation.Approve);
        result.Should().BeFalse();
    }

    [HumansFact]
    public async Task Submitter_CannotEndorse_OwnReport()
    {
        var report = MakeReport(SubmitterId, ExpenseReportStatus.Submitted);
        var result = await EvaluateAsync(CreateUser(SubmitterId), report, ExpenseReportOperation.Endorse);
        result.Should().BeFalse();
    }

    // ─── Coordinator (this category) × View / Endorse / CoordinatorReject ───

    [HumansFact]
    public async Task Coordinator_CanView_ReportInTheirCategory()
    {
        var report = MakeReport(SubmitterId, ExpenseReportStatus.Submitted);
        var result = await EvaluateAsync(CreateUser(CoordinatorId), report, ExpenseReportOperation.View);
        result.Should().BeTrue();
    }

    [HumansFact]
    public async Task Coordinator_CanEndorse_SubmittedReportInTheirCategory()
    {
        var report = MakeReport(SubmitterId, ExpenseReportStatus.Submitted);
        var result = await EvaluateAsync(CreateUser(CoordinatorId), report, ExpenseReportOperation.Endorse);
        result.Should().BeTrue();
    }

    [HumansFact]
    public async Task Coordinator_CanCoordinatorReject_SubmittedReportInTheirCategory()
    {
        var report = MakeReport(SubmitterId, ExpenseReportStatus.Submitted);
        var result = await EvaluateAsync(CreateUser(CoordinatorId), report, ExpenseReportOperation.CoordinatorReject);
        result.Should().BeTrue();
    }

    [HumansFact]
    public async Task Coordinator_CannotEndorse_DraftReport()
    {
        var report = MakeReport(SubmitterId, ExpenseReportStatus.Draft);
        var result = await EvaluateAsync(CreateUser(CoordinatorId), report, ExpenseReportOperation.Endorse);
        result.Should().BeFalse();
    }

    [HumansFact]
    public async Task Coordinator_CannotEndorse_AlreadyEndorsedReport()
    {
        var report = MakeReport(SubmitterId, ExpenseReportStatus.CoordinatorEndorsed);
        var result = await EvaluateAsync(CreateUser(CoordinatorId), report, ExpenseReportOperation.Endorse);
        result.Should().BeFalse();
    }

    // ─── Coordinator cannot approve or edit ───────────────────────────────────

    [HumansFact]
    public async Task Coordinator_CannotApprove_SubmittedReport()
    {
        var report = MakeReport(SubmitterId, ExpenseReportStatus.Submitted);
        var result = await EvaluateAsync(CreateUser(CoordinatorId), report, ExpenseReportOperation.Approve);
        result.Should().BeFalse();
    }

    [HumansFact]
    public async Task Coordinator_CannotEdit_AnyReport()
    {
        var report = MakeReport(SubmitterId, ExpenseReportStatus.Submitted);
        var result = await EvaluateAsync(CreateUser(CoordinatorId), report, ExpenseReportOperation.Edit);
        result.Should().BeFalse();
    }

    // ─── Coordinator (other category) × all denied ───────────────────────────

    [HumansFact]
    public async Task OtherCoordinator_CannotView_ReportNotInTheirCategory()
    {
        var report = MakeReport(SubmitterId, ExpenseReportStatus.Submitted);
        var result = await EvaluateAsync(CreateUser(OtherCoordinatorId), report, ExpenseReportOperation.View);
        result.Should().BeFalse();
    }

    [HumansFact]
    public async Task OtherCoordinator_CannotEndorse_ReportNotInTheirCategory()
    {
        var report = MakeReport(SubmitterId, ExpenseReportStatus.Submitted);
        var result = await EvaluateAsync(CreateUser(OtherCoordinatorId), report, ExpenseReportOperation.Endorse);
        result.Should().BeFalse();
    }

    // ─── FinanceAdmin × View / Approve / FinanceReject ───────────────────────

    [HumansFact]
    public async Task FinanceAdmin_CanView_AnyReport()
    {
        var report = MakeReport(SubmitterId, ExpenseReportStatus.Submitted);
        var result = await EvaluateAsync(CreateUserWithRole(RoleNames.FinanceAdmin), report, ExpenseReportOperation.View);
        result.Should().BeTrue();
    }

    [HumansFact]
    public async Task FinanceAdmin_CanApprove_SubmittedReport()
    {
        var report = MakeReport(SubmitterId, ExpenseReportStatus.Submitted);
        var result = await EvaluateAsync(CreateUserWithRole(RoleNames.FinanceAdmin), report, ExpenseReportOperation.Approve);
        result.Should().BeTrue();
    }

    [HumansFact]
    public async Task FinanceAdmin_CanFinanceReject_SubmittedReport()
    {
        var report = MakeReport(SubmitterId, ExpenseReportStatus.Submitted);
        var result = await EvaluateAsync(CreateUserWithRole(RoleNames.FinanceAdmin), report, ExpenseReportOperation.FinanceReject);
        result.Should().BeTrue();
    }

    // ─── FinanceAdmin cannot edit ─────────────────────────────────────────────

    [HumansFact]
    public async Task FinanceAdmin_CannotEdit_AnyReport()
    {
        var report = MakeReport(SubmitterId, ExpenseReportStatus.Submitted);
        var result = await EvaluateAsync(CreateUserWithRole(RoleNames.FinanceAdmin), report, ExpenseReportOperation.Edit);
        result.Should().BeFalse();
    }

    [HumansFact]
    public async Task FinanceAdmin_CanEndorse_SubmittedReport()
    {
        // Per docs/sections/Expenses.md actors table: FinanceAdmin has all coordinator
        // capabilities, including Endorse (gated to Submitted status).
        var report = MakeReport(SubmitterId, ExpenseReportStatus.Submitted);
        var result = await EvaluateAsync(CreateUserWithRole(RoleNames.FinanceAdmin), report, ExpenseReportOperation.Endorse);
        result.Should().BeTrue();
    }

    // ─── Admin × all privileged operations ────────────────────────────────────

    [HumansFact]
    public async Task Admin_CanView_AnyReport()
    {
        var report = MakeReport(SubmitterId, ExpenseReportStatus.Draft);
        var result = await EvaluateAsync(CreateUserWithRole(RoleNames.Admin), report, ExpenseReportOperation.View);
        result.Should().BeTrue();
    }

    [HumansFact]
    public async Task Admin_CanApprove_AnyReport()
    {
        var report = MakeReport(SubmitterId, ExpenseReportStatus.Submitted);
        var result = await EvaluateAsync(CreateUserWithRole(RoleNames.Admin), report, ExpenseReportOperation.Approve);
        result.Should().BeTrue();
    }

    [HumansFact]
    public async Task Admin_CannotEdit_SomeoneElsesReport()
    {
        var report = MakeReport(SubmitterId, ExpenseReportStatus.Draft);
        var adminId = Guid.NewGuid();
        var result = await EvaluateAsync(CreateUserWithRoleAndId(RoleNames.Admin, adminId), report, ExpenseReportOperation.Edit);
        result.Should().BeFalse();
    }

    // ─── Random user × all denied ────────────────────────────────────────────

    [HumansFact]
    public async Task RandomUser_CannotView_AnyReport()
    {
        var report = MakeReport(SubmitterId, ExpenseReportStatus.Submitted);
        var result = await EvaluateAsync(CreateUser(RandomUserId), report, ExpenseReportOperation.View);
        result.Should().BeFalse();
    }

    [HumansFact]
    public async Task RandomUser_CannotApprove_AnyReport()
    {
        var report = MakeReport(SubmitterId, ExpenseReportStatus.Submitted);
        var result = await EvaluateAsync(CreateUser(RandomUserId), report, ExpenseReportOperation.Approve);
        result.Should().BeFalse();
    }

    // ─── Unauthenticated ──────────────────────────────────────────────────────

    [HumansFact]
    public async Task Unauthenticated_CannotView_AnyReport()
    {
        var report = MakeReport(SubmitterId, ExpenseReportStatus.Submitted);
        var unauthenticated = new ClaimsPrincipal(new ClaimsIdentity());
        var result = await EvaluateAsync(unauthenticated, report, ExpenseReportOperation.View);
        result.Should().BeFalse();
    }

    // ─── Category with no team (coordinator lookup short-circuits) ────────────

    [HumansFact]
    public async Task CoordinatorCheck_DeniedWhen_CategoryHasNoTeam()
    {
        // Category without a team — coordinator access is not possible
        _budgetService.GetCategoryByIdAsync(CategoryId)
            .Returns(new BudgetCategory { Id = CategoryId, TeamId = null });

        var report = MakeReport(SubmitterId, ExpenseReportStatus.Submitted);
        var result = await EvaluateAsync(CreateUser(CoordinatorId), report, ExpenseReportOperation.Endorse);
        result.Should().BeFalse();
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private async Task<bool> EvaluateAsync(
        ClaimsPrincipal user,
        ExpenseReportDto resource,
        ExpenseReportOperation op)
    {
        var requirement = new ExpenseReportOperationRequirement(op);
        var context = new AuthorizationHandlerContext([requirement], user, resource);
        await _handler.HandleAsync(context);
        return context.HasSucceeded;
    }

    private static ExpenseReportDto MakeReport(Guid submitterId, ExpenseReportStatus status)
    {
        return new ExpenseReportDto
        {
            Id = Guid.NewGuid(),
            SubmitterUserId = submitterId,
            BudgetCategoryId = CategoryId,
            BudgetYearId = Guid.NewGuid(),
            Status = status,
            PayeeName = "Test Payee",
            PayeeIban = "ES9121000418450200051332",
            Total = 100m,
            CreatedAt = Instant.MinValue,
            UpdatedAt = Instant.MinValue,
            Lines = []
        };
    }

    private static ClaimsPrincipal CreateUser(Guid userId)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(ClaimTypes.Name, "user@example.com")
        };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
    }

    private static ClaimsPrincipal CreateUserWithRole(string role)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Name, "admin@example.com"),
            new(ClaimTypes.Role, role)
        };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
    }

    private static ClaimsPrincipal CreateUserWithRoleAndId(string role, Guid userId)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(ClaimTypes.Name, "admin@example.com"),
            new(ClaimTypes.Role, role)
        };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
    }
}
