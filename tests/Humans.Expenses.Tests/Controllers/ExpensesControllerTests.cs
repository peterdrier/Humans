using System.Security.Claims;
using AwesomeAssertions;
using Humans.Budget.Contracts;
using Humans.Base.Constants;
using Humans.Expenses.Authorization;
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

public sealed class ExpensesControllerTests
{
    private static readonly FakeClock TestClock = new(Instant.FromUtc(2026, 5, 10, 12, 0));

    [HumansTheory]
    [Xunit.InlineData("Iban", true)]
    [Xunit.InlineData("Iban", false)]
    [Xunit.InlineData("LineEdit", true)]
    [Xunit.InlineData("LineEdit", false)]
    [Xunit.InlineData("LineProofs", true)]
    [Xunit.InlineData("LineProofs", false)]
    public async Task Historical_report_owner_pages_follow_the_merge_chain(string page, bool isMergedSubmitter)
    {
        var actorId = Guid.NewGuid();
        var sourceId = Guid.NewGuid();
        var reportId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        var user = UserInfo.Create(new User { Id = actorId }, [], [], [], null, [])
            with
        { MergedUserIds = [isMergedSubmitter ? sourceId : Guid.NewGuid()] };
        var users = Substitute.For<IUserService>();
        users.GetUserInfoAsync(actorId, Arg.Any<CancellationToken>()).Returns(user);
        users.GetUserInfoAsync(sourceId, Arg.Any<CancellationToken>()).Returns(user);
        var reports = Substitute.For<IExpenseReportManagement>();
        reports.GetAsync(reportId, Arg.Any<CancellationToken>()).Returns(new ExpenseReportDto
        {
            Id = reportId,
            SubmitterUserId = sourceId,
            Status = ExpenseReportStatus.Approved,
            HoldedAccountNumber = 0,
            BudgetCategoryId = null,
            BudgetYearId = null,
            PayeeName = "Historical name",
            PayeeIban = "",
            Total = 10m,
            CreatedAt = default,
            UpdatedAt = default,
            Lines = [new ExpenseLineDto
            {
                Id = lineId, ExpenseReportId = reportId, Description = "Invoice", Amount = 10m,
                LineType = ExpenseLineType.Invoice, SortOrder = 0
            }]
        });
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Failed());
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, actorId.ToString())], "test"))
        };
        var controller = new ExpensesController(users, reports, Substitute.For<IBudgetService>(),
            Substitute.For<IHoldedFinanceService>(), Substitute.For<IFinanceService>(), Substitute.For<IPreapprovalService>(), authorization,
            TestClock, NullLogger<ExpensesController>.Instance, Substitute.For<IStringLocalizer<ExpensesResource>>())
        {
            ControllerContext = new ControllerContext { HttpContext = http },
            TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>())
        };

        var result = page switch
        {
            "Iban" => await controller.Iban(reportId),
            "LineEdit" => await controller.LineEdit(reportId, lineId),
            _ => await controller.LineProofs(reportId, lineId)
        };

        if (isMergedSubmitter)
        {
            var model = result.Should().BeOfType<ViewResult>().Which.Model;
            if (model is ExpenseLineEditViewModel line) line.CanEditLines.Should().BeFalse();
            if (model is ExpenseLineProofsViewModel proofs) proofs.CanEditLines.Should().BeFalse();
            if (model is ExpenseIbanViewModel iban) iban.MemberName.Should().BeNull();
        }
        else
            result.Should().BeOfType<ForbidResult>();
    }

    [HumansTheory]
    [Xunit.InlineData("Detail", "View")]
    [Xunit.InlineData("EditGet", "Edit")]
    [Xunit.InlineData("EditPost", "Edit")]
    [Xunit.InlineData("NewLine", "Edit")]
    [Xunit.InlineData("AddLine", "Edit")]
    [Xunit.InlineData("LineEdit", "Edit")]
    [Xunit.InlineData("LineProofs", "Edit")]
    [Xunit.InlineData("UpdateLine", "Edit")]
    [Xunit.InlineData("RemoveLine", "Edit")]
    [Xunit.InlineData("AttachFile", "Edit")]
    [Xunit.InlineData("RemoveAttachment", "Edit")]
    [Xunit.InlineData("Submit", "Submit")]
    [Xunit.InlineData("Withdraw", "Withdraw")]
    [Xunit.InlineData("IbanGet", "Edit")]
    [Xunit.InlineData("IbanPost", "Edit")]
    [Xunit.InlineData("Endorse", "Endorse")]
    [Xunit.InlineData("CoordinatorReject", "CoordinatorReject")]
    [Xunit.InlineData("Approve", "Approve")]
    [Xunit.InlineData("Reject", "FinanceReject")]
    [Xunit.InlineData("HoldedRetry", "RequeueHoldedPush")]
    public async Task Report_actions_stop_on_missing_actor_missing_report_and_denied_access(
        string action, string expectedOperation)
    {
        var actorId = Guid.NewGuid();
        var reportId = Guid.NewGuid();
        var users = Substitute.For<IUserService>();
        var reports = Substitute.For<IExpenseReportManagement>();
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(),
                Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Failed());
        var controller = new ExpensesController(users, reports,
            Substitute.For<IBudgetService>(), Substitute.For<IHoldedFinanceService>(), Substitute.For<IFinanceService>(),
            Substitute.For<IPreapprovalService>(), authorization, TestClock,
            NullLogger<ExpensesController>.Instance, Substitute.For<IStringLocalizer<ExpensesResource>>())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, actorId.ToString())], "test"))
                }
            }
        };

        // Missing actor must not expose or fetch a report.
        (await InvokeAsync(controller, action, reportId)).Should().BeOfType<NotFoundResult>();
        reports.ReceivedCalls().Should().BeEmpty();
        users.GetUserInfoAsync(actorId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(UserInfo.Create(
                new User { Id = actorId, PreferredLanguage = "en" }, [], [], [], null, [])));

        // A missing report must not reach authorization or any mutation.
        (await InvokeAsync(controller, action, reportId)).Should().BeOfType<NotFoundResult>();
        authorization.ReceivedCalls().Should().BeEmpty();
        reports.ClearReceivedCalls();
        reports.GetAsync(reportId).Returns(new ExpenseReportDto
        {
            Id = reportId,
            SubmitterUserId = Guid.NewGuid(),
            HoldedAccountNumber = 0,
            BudgetCategoryId = Guid.NewGuid(),
            BudgetYearId = Guid.NewGuid(),
            Status = ExpenseReportStatus.Draft,
            PayeeName = "Submitter",
            PayeeIban = "",
            Total = 0,
            CreatedAt = default,
            UpdatedAt = default,
            Lines = []
        });

        (await InvokeAsync(controller, action, reportId)).Should().BeOfType<ForbidResult>();
        reports.ReceivedCalls().Should().ContainSingle(call =>
            call.GetMethodInfo().Name == nameof(IExpenseReportManagement.GetAsync));
        var operations = authorization.ReceivedCalls()
            .SelectMany(call => call.GetArguments().OfType<IEnumerable<IAuthorizationRequirement>>()
                .SelectMany(requirements => requirements))
            .OfType<ExpenseReportOperationRequirement>()
            .Select(requirement => requirement.Operation.ToString()).ToList();
        operations.Should().Contain(expectedOperation);
    }

    [HumansTheory]
    [Xunit.InlineData(ExpenseReportStatus.Draft, true, false)]
    [Xunit.InlineData(ExpenseReportStatus.Submitted, true, true)]
    [Xunit.InlineData(ExpenseReportStatus.CoordinatorEndorsed, true, true)]
    [Xunit.InlineData(ExpenseReportStatus.Approved, true, false)]
    [Xunit.InlineData(ExpenseReportStatus.Withdrawn, true, false)]
    [Xunit.InlineData(ExpenseReportStatus.Submitted, false, false)]
    [Xunit.InlineData(ExpenseReportStatus.Approved, false, false)]
    public async Task Withdraw_UsesResourcePolicyBeforeMutation(
        ExpenseReportStatus status, bool isSubmitter, bool allowed)
    {
        var actorId = Guid.NewGuid();
        var reportId = Guid.NewGuid();
        var users = Substitute.For<IUserService>();
        users.GetUserInfoAsync(actorId, Arg.Any<CancellationToken>())
            .Returns(UserInfo.Create(new User { Id = actorId }, [], [], [], null, []));
        var report = new ExpenseReportDto
        {
            Id = reportId,
            SubmitterUserId = isSubmitter ? actorId : Guid.NewGuid(),
            HoldedAccountNumber = 0,
            BudgetCategoryId = Guid.NewGuid(),
            BudgetYearId = Guid.NewGuid(),
            Status = status,
            PayeeName = "Submitter",
            PayeeIban = "",
            Total = 0,
            CreatedAt = default,
            UpdatedAt = default,
            Lines = []
        };
        var reports = Substitute.For<IExpenseReportManagement>();
        reports.GetAsync(reportId).Returns(report);
        reports.WithdrawWithResultAsync(reportId, actorId).Returns(ExpenseMutationResult.Success);
        var handler = new ExpenseReportAuthorizationHandler(
            Substitute.For<IBudgetService>(), Substitute.For<Humans.Teams.Contracts.ITeamService>(), users,
            Substitute.For<IPreapprovalService>());
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), report,
                Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(async call =>
            {
                var context = new AuthorizationHandlerContext(
                    call.Arg<IEnumerable<IAuthorizationRequirement>>(), call.Arg<ClaimsPrincipal>(), report);
                await handler.HandleAsync(context);
                return context.HasSucceeded ? AuthorizationResult.Success() : AuthorizationResult.Failed();
            });
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, actorId.ToString()),
                new Claim(ClaimTypes.Role, RoleNames.FinanceAdmin)], "test"))
        };
        var localizer = Substitute.For<IStringLocalizer<ExpensesResource>>();
        localizer[Arg.Any<string>()].Returns(call => new LocalizedString(call.Arg<string>(), call.Arg<string>()));
        var controller = new ExpensesController(users, reports, Substitute.For<IBudgetService>(),
            Substitute.For<IHoldedFinanceService>(), Substitute.For<IFinanceService>(), Substitute.For<IPreapprovalService>(), authorization,
            TestClock, NullLogger<ExpensesController>.Instance, localizer)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
            TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>())
        };

        var result = await controller.Withdraw(reportId);

        if (allowed)
        {
            result.Should().BeOfType<RedirectToActionResult>();
            await reports.Received(1).WithdrawWithResultAsync(reportId, actorId);
        }
        else
        {
            result.Should().BeOfType<ForbidResult>();
            await reports.DidNotReceive().WithdrawWithResultAsync(reportId, actorId);
        }
    }

    [HumansTheory]
    [Xunit.InlineData(true, false, "Expenses_Iban_Saved", "IBAN guardado.")]
    [Xunit.InlineData(false, true, "Expenses_Iban_InvalidFormat", "Formato de IBAN no válido.")]
    [Xunit.InlineData(false, false, "Expenses_Iban_SaveFailed", "No se pudo guardar el IBAN.")]
    public async Task IbanPost_LocalizesSuccessValidationAndFailureMessages(
        bool succeeded, bool validationError, string messageKey, string translated)
    {
        var actorId = Guid.NewGuid();
        var reportId = Guid.NewGuid();
        var users = Substitute.For<IUserService>();
        users.GetUserInfoAsync(actorId, Arg.Any<CancellationToken>()).Returns(
            UserInfo.Create(new User { Id = actorId }, [], [], [], null, []));
        var reports = Substitute.For<IExpenseReportManagement>();
        reports.GetAsync(reportId).Returns(new ExpenseReportDto
        {
            Id = reportId,
            SubmitterUserId = actorId,
            HoldedAccountNumber = 0,
            BudgetCategoryId = Guid.NewGuid(),
            BudgetYearId = Guid.NewGuid(),
            Status = ExpenseReportStatus.Draft,
            PayeeName = "Submitter",
            PayeeIban = "",
            Total = 0,
            CreatedAt = default,
            UpdatedAt = default,
            Lines = []
        });
        reports.SaveSubmitterIbanWithResultAsync(reportId, actorId, Arg.Any<string?>())
            .Returns(new ExpenseIbanSaveResult(succeeded, validationError, messageKey));
        var localizer = Substitute.For<IStringLocalizer<ExpensesResource>>();
        localizer[messageKey].Returns(new LocalizedString(messageKey, translated));
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, actorId.ToString())], "test"))
        };
        var controller = new ExpensesController(users, reports,
            Substitute.For<IBudgetService>(), Substitute.For<IHoldedFinanceService>(), Substitute.For<IFinanceService>(),
            Substitute.For<IPreapprovalService>(), Substitute.For<IAuthorizationService>(), TestClock,
            NullLogger<ExpensesController>.Instance, localizer)
        {
            ControllerContext = new ControllerContext { HttpContext = context },
            TempData = new TempDataDictionary(context, Substitute.For<ITempDataProvider>())
        };

        var result = await controller.Iban(reportId, new ExpenseIbanViewModel());

        if (succeeded)
        {
            result.Should().BeOfType<RedirectToActionResult>();
            controller.TempData[TempDataKeys.SuccessMessage].Should().Be(translated);
        }
        else
        {
            result.Should().BeOfType<ViewResult>();
            if (validationError)
                controller.ModelState[nameof(ExpenseIbanViewModel.Iban)]!.Errors.Should()
                    .ContainSingle().Which.ErrorMessage.Should().Be(translated);
            else
                controller.TempData[TempDataKeys.ErrorMessage].Should().Be(translated);
        }
    }

    [HumansFact]
    public async Task Index_ShowsTheReviewLink_ToAnyoneWithAClaimToSignOff()
    {
        // A child-team coordinator holds no department, yet can endorse; the link must follow.
        var actorId = Guid.NewGuid();
        var users = Substitute.For<IUserService>();
        users.GetUserInfoAsync(actorId, Arg.Any<CancellationToken>()).Returns(
            UserInfo.Create(new User { Id = actorId }, [], [], [], null, []));
        var reports = Substitute.For<IExpenseReportManagement>();
        reports.GetForSubmitterAsync(actorId, Arg.Any<CancellationToken>()).Returns([]);
        reports.GetCoordinatorQueueAsync(actorId, Arg.Any<CancellationToken>()).Returns([new ExpenseReportDto
        {
            Id = Guid.NewGuid(),
            SubmitterUserId = Guid.NewGuid(),
            BudgetCategoryId = Guid.NewGuid(),
            BudgetYearId = Guid.NewGuid(),
            HoldedAccountNumber = 0,
            Status = ExpenseReportStatus.Submitted,
            PayeeName = "Submitter",
            PayeeIban = "",
            Total = 0,
            CreatedAt = default,
            UpdatedAt = default,
            Lines = []
        }]);
        var budget = Substitute.For<IBudgetService>();
        budget.GetEffectiveCoordinatorTeamIdsAsync(actorId, Arg.Any<CancellationToken>()).Returns(new HashSet<Guid>());
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, actorId.ToString())], "test"))
        };
        var controller = new ExpensesController(users, reports, budget,
            Substitute.For<IHoldedFinanceService>(), Substitute.For<IFinanceService>(), Substitute.For<IPreapprovalService>(),
            Substitute.For<IAuthorizationService>(), TestClock,
            NullLogger<ExpensesController>.Instance, Substitute.For<IStringLocalizer<ExpensesResource>>())
        {
            ControllerContext = new ControllerContext { HttpContext = context },
            TempData = new TempDataDictionary(context, Substitute.For<ITempDataProvider>())
        };

        var result = await controller.Index();

        result.Should().BeOfType<ViewResult>()
            .Which.Model.Should().BeOfType<ExpensesIndexViewModel>()
            .Which.IsCoordinator.Should().BeTrue();
    }

    [HumansFact]
    public async Task Index_LabelsLegacyReportsFromTheirBookedYear()
    {
        var actorId = Guid.NewGuid();
        var yearId = Guid.NewGuid();
        var groupId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var users = Substitute.For<IUserService>();
        users.GetUserInfoAsync(actorId, Arg.Any<CancellationToken>())
            .Returns(UserInfo.Create(new User { Id = actorId }, [], [], [], null, []));
        var first = new ExpenseReportDto
        {
            Id = Guid.NewGuid(),
            SubmitterUserId = actorId,
            BudgetYearId = yearId,
            BudgetCategoryId = categoryId,
            HoldedAccountNumber = 0,
            Status = ExpenseReportStatus.Approved,
            PayeeName = "Submitter",
            PayeeIban = "",
            Total = 10,
            CreatedAt = default,
            UpdatedAt = default,
            Lines = []
        };
        var second = first with { Id = Guid.NewGuid() };
        var reports = Substitute.For<IExpenseReportManagement>();
        reports.GetForSubmitterAsync(actorId, Arg.Any<CancellationToken>()).Returns([first, second]);
        reports.GetCoordinatorQueueAsync(actorId, Arg.Any<CancellationToken>()).Returns([]);
        var budget = Substitute.For<IBudgetService>();
        budget.GetActiveYearAsync(Arg.Any<CancellationToken>())
            .Returns(new BudgetYearDetail(Guid.NewGuid(), "2026", "Current", BudgetYearStatus.Active, false, []));
        budget.GetYearByIdAsync(yearId, Arg.Any<CancellationToken>()).Returns(new BudgetYearDetail(yearId, "2025", "Old", BudgetYearStatus.Closed, false,
            [new BudgetGroupDetail(groupId, yearId, "Old group", 0, false, false, false, null,
                [new BudgetCategoryDetail(categoryId, groupId, "Old category", 0, ExpenditureType.OpEx, null, 0, [])])]));
        budget.GetEffectiveCoordinatorTeamIdsAsync(actorId, Arg.Any<CancellationToken>()).Returns(new HashSet<Guid>());
        var localizer = Substitute.For<IStringLocalizer<ExpensesResource>>();
        localizer[Arg.Any<string>()].Returns(call => new LocalizedString(call.Arg<string>(), call.Arg<string>()));
        using var request = new CancellationTokenSource();
        var context = new DefaultHttpContext
        {
            RequestAborted = request.Token,
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, actorId.ToString())], "test"))
        };
        var controller = new ExpensesController(users, reports, budget,
            Substitute.For<IHoldedFinanceService>(), Substitute.For<IFinanceService>(), Substitute.For<IPreapprovalService>(),
            Substitute.For<IAuthorizationService>(), TestClock,
            NullLogger<ExpensesController>.Instance, localizer)
        {
            ControllerContext = new ControllerContext { HttpContext = context },
            TempData = new TempDataDictionary(context, Substitute.For<ITempDataProvider>())
        };

        var result = await controller.Index();

        var model = result.Should().BeOfType<ViewResult>().Which.Model.Should().BeOfType<ExpensesIndexViewModel>().Which;
        model.BookingLabels[first.Id].Should().Be("Old group / Old category");
        model.BookingLabels[second.Id].Should().Be("Old group / Old category");
        await budget.Received(1).GetYearByIdAsync(yearId, context.RequestAborted);
        await budget.Received(1).GetEffectiveCoordinatorTeamIdsAsync(actorId, context.RequestAborted);
    }

    private static Task<IActionResult> InvokeAsync(ExpensesController controller, string action, Guid id) =>
        action switch
        {
            "Detail" => controller.Detail(id),
            "EditGet" => controller.Edit(id),
            "EditPost" => controller.Edit(id, new ExpenseEditViewModel()),
            "NewLine" => controller.NewLine(id),
            "AddLine" => controller.AddLine(id, new AddLineInputModel(), null),
            "LineEdit" => controller.LineEdit(id, Guid.NewGuid()),
            "LineProofs" => controller.LineProofs(id, Guid.NewGuid()),
            "UpdateLine" => controller.UpdateLine(id, new EditLineInputModel()),
            "RemoveLine" => controller.RemoveLine(id, Guid.NewGuid()),
            "AttachFile" => controller.AttachFile(id, Guid.NewGuid(), null),
            "RemoveAttachment" => controller.RemoveAttachment(id, Guid.NewGuid()),
            "Submit" => controller.Submit(id),
            "Withdraw" => controller.Withdraw(id),
            "IbanGet" => controller.Iban(id),
            "IbanPost" => controller.Iban(id, new ExpenseIbanViewModel()),
            "Endorse" => controller.Endorse(id, new EndorseInputModel()),
            "CoordinatorReject" => controller.CoordinatorReject(id, new CoordinatorRejectInputModel()),
            "Approve" => controller.Approve(id, new ApproveInputModel()),
            "Reject" => controller.Reject(id, new FinanceRejectInputModel()),
            "HoldedRetry" => controller.HoldedRetry(id),
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };
}
