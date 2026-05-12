using System.Security.Claims;
using AwesomeAssertions;
using Humans.Application.Interfaces.Expenses;
using Humans.Application.Services.Expenses.Dtos;
using Humans.Domain.Constants;
using Humans.Domain.Enums;
using Humans.Web.Authorization.Requirements;
using Microsoft.AspNetCore.Authorization;
using NSubstitute;
using NodaTime;

namespace Humans.Application.Tests.Authorization;

/// <summary>
/// Unit tests for IbanAccessHandler.
/// Covers: self/granted, FinanceAdmin-with-report-context/granted,
/// FinanceAdmin-without-report-context/denied, Admin-on-admin-page/granted,
/// random user/denied.
/// </summary>
public sealed class IbanAccessHandlerTests
{
    private readonly IExpenseReportService _expenseService = Substitute.For<IExpenseReportService>();
    private readonly IbanAccessHandler _handler;

    private static readonly Guid TargetUserId = Guid.NewGuid();
    private static readonly Guid ReportId = Guid.NewGuid();

    public IbanAccessHandlerTests()
    {
        _handler = new IbanAccessHandler(_expenseService);

        // Default: report exists in Submitted status (non-Draft, non-Withdrawn)
        _expenseService.GetAsync(ReportId, Arg.Any<CancellationToken>())
            .Returns(MakeReport(ReportId, ExpenseReportStatus.Submitted));
    }

    // ─── Self access ──────────────────────────────────────────────────────────

    [HumansFact]
    public async Task Self_CanAccess_OwnIban()
    {
        var user = CreateUser(TargetUserId);
        var requirement = new IbanAccessRequirement(TargetUserId);

        var result = await EvaluateAsync(user, requirement);

        result.Should().BeTrue();
    }

    [HumansFact]
    public async Task Self_CanAccess_OwnIban_WithReportContext()
    {
        var user = CreateUser(TargetUserId);
        var requirement = new IbanAccessRequirement(TargetUserId, reportId: ReportId);

        var result = await EvaluateAsync(user, requirement);

        result.Should().BeTrue();
    }

    // ─── FinanceAdmin with report context ─────────────────────────────────────

    [HumansFact]
    public async Task FinanceAdmin_CanAccess_IbanInReportContext_NonDraftNonWithdrawn()
    {
        var user = CreateUserWithRole(RoleNames.FinanceAdmin);
        var requirement = new IbanAccessRequirement(TargetUserId, reportId: ReportId);

        var result = await EvaluateAsync(user, requirement);

        result.Should().BeTrue();
    }

    [HumansFact]
    public async Task FinanceAdmin_CanAccess_Iban_WhenReportIsCoordinatorEndorsed()
    {
        _expenseService.GetAsync(ReportId, Arg.Any<CancellationToken>())
            .Returns(MakeReport(ReportId, ExpenseReportStatus.CoordinatorEndorsed));
        var user = CreateUserWithRole(RoleNames.FinanceAdmin);
        var requirement = new IbanAccessRequirement(TargetUserId, reportId: ReportId);

        var result = await EvaluateAsync(user, requirement);

        result.Should().BeTrue();
    }

    [HumansFact]
    public async Task FinanceAdmin_CanAccess_Iban_WhenReportIsApproved()
    {
        _expenseService.GetAsync(ReportId, Arg.Any<CancellationToken>())
            .Returns(MakeReport(ReportId, ExpenseReportStatus.Approved));
        var user = CreateUserWithRole(RoleNames.FinanceAdmin);
        var requirement = new IbanAccessRequirement(TargetUserId, reportId: ReportId);

        var result = await EvaluateAsync(user, requirement);

        result.Should().BeTrue();
    }

    // ─── FinanceAdmin denied when report is Draft or Withdrawn ────────────────

    [HumansFact]
    public async Task FinanceAdmin_CannotAccess_Iban_WhenReportIsDraft()
    {
        _expenseService.GetAsync(ReportId, Arg.Any<CancellationToken>())
            .Returns(MakeReport(ReportId, ExpenseReportStatus.Draft));
        var user = CreateUserWithRole(RoleNames.FinanceAdmin);
        var requirement = new IbanAccessRequirement(TargetUserId, reportId: ReportId);

        var result = await EvaluateAsync(user, requirement);

        result.Should().BeFalse();
    }

    [HumansFact]
    public async Task FinanceAdmin_CannotAccess_Iban_WhenReportIsWithdrawn()
    {
        _expenseService.GetAsync(ReportId, Arg.Any<CancellationToken>())
            .Returns(MakeReport(ReportId, ExpenseReportStatus.Withdrawn));
        var user = CreateUserWithRole(RoleNames.FinanceAdmin);
        var requirement = new IbanAccessRequirement(TargetUserId, reportId: ReportId);

        var result = await EvaluateAsync(user, requirement);

        result.Should().BeFalse();
    }

    // ─── FinanceAdmin without report context (no ID) ──────────────────────────

    [HumansFact]
    public async Task FinanceAdmin_CannotAccess_Iban_WithoutReportContext()
    {
        var user = CreateUserWithRole(RoleNames.FinanceAdmin);
        // No reportId — no report context, not an admin page
        var requirement = new IbanAccessRequirement(TargetUserId);

        var result = await EvaluateAsync(user, requirement);

        result.Should().BeFalse();
    }

    // ─── Admin on admin page ──────────────────────────────────────────────────

    [HumansFact]
    public async Task Admin_CanAccess_Iban_OnAdminPage()
    {
        var user = CreateUserWithRole(RoleNames.Admin);
        var requirement = new IbanAccessRequirement(TargetUserId, isAdminPageContext: true);

        var result = await EvaluateAsync(user, requirement);

        result.Should().BeTrue();
    }

    [HumansFact]
    public async Task FinanceAdmin_CannotAccess_Iban_OnAdminPage_WhenNotAdmin()
    {
        // FinanceAdmin alone cannot use admin page context — that's Admin-only
        var user = CreateUserWithRole(RoleNames.FinanceAdmin);
        var requirement = new IbanAccessRequirement(TargetUserId, isAdminPageContext: true);

        var result = await EvaluateAsync(user, requirement);

        // FinanceAdmin is not Admin, so admin-page context doesn't help without report
        result.Should().BeFalse();
    }

    // ─── Random user denied in all cases ─────────────────────────────────────

    [HumansFact]
    public async Task RandomUser_CannotAccess_AnyIban()
    {
        var user = CreateUser(Guid.NewGuid()); // different from TargetUserId
        var requirement = new IbanAccessRequirement(TargetUserId);

        var result = await EvaluateAsync(user, requirement);

        result.Should().BeFalse();
    }

    [HumansFact]
    public async Task RandomUser_CannotAccess_IbanEvenWithReportId()
    {
        var user = CreateUser(Guid.NewGuid());
        var requirement = new IbanAccessRequirement(TargetUserId, reportId: ReportId);

        var result = await EvaluateAsync(user, requirement);

        result.Should().BeFalse();
    }

    // ─── Unauthenticated ──────────────────────────────────────────────────────

    [HumansFact]
    public async Task Unauthenticated_CannotAccess_AnyIban()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity());
        var requirement = new IbanAccessRequirement(TargetUserId);

        var result = await EvaluateAsync(user, requirement);

        result.Should().BeFalse();
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private async Task<bool> EvaluateAsync(ClaimsPrincipal user, IbanAccessRequirement requirement)
    {
        var context = new AuthorizationHandlerContext([requirement], user, null);
        await _handler.HandleAsync(context);
        return context.HasSucceeded;
    }

    private static ExpenseReportDto MakeReport(Guid id, ExpenseReportStatus status)
    {
        return new ExpenseReportDto
        {
            Id = id,
            SubmitterUserId = TargetUserId,
            BudgetCategoryId = Guid.NewGuid(),
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
}
