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
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Humans.Expenses.Tests.Controllers;

public sealed class ExpensesControllerTests
{
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
    [Xunit.InlineData("Withdraw", null)]
    [Xunit.InlineData("IbanGet", "Edit")]
    [Xunit.InlineData("IbanPost", "Edit")]
    [Xunit.InlineData("Endorse", "Endorse")]
    [Xunit.InlineData("CoordinatorReject", "CoordinatorReject")]
    [Xunit.InlineData("Approve", "Approve")]
    [Xunit.InlineData("Reject", "FinanceReject")]
    [Xunit.InlineData("HoldedRetry", "RequeueHoldedPush")]
    public async Task Report_actions_stop_on_missing_actor_missing_report_and_denied_access(
        string action, string? expectedOperation)
    {
        var actorId = Guid.NewGuid();
        var reportId = Guid.NewGuid();
        var users = Substitute.For<IUserServiceRead>();
        var reports = Substitute.For<IExpenseReportService>();
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(),
                Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Failed());
        var controller = new ExpensesController(users, reports,
            Substitute.For<IBudgetServiceRead>(), Substitute.For<IHoldedFinanceServiceRead>(),
            authorization, NullLogger<ExpensesController>.Instance,
            Substitute.For<IStringLocalizer<ExpensesResource>>())
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
            call.GetMethodInfo().Name == nameof(IExpenseReportService.GetAsync));
        var operations = authorization.ReceivedCalls()
            .SelectMany(call => call.GetArguments().OfType<IEnumerable<IAuthorizationRequirement>>()
                .SelectMany(requirements => requirements))
            .OfType<ExpenseReportOperationRequirement>()
            .Select(requirement => requirement.Operation.ToString()).ToList();
        if (expectedOperation is null)
            operations.Should().BeEmpty("withdrawal retains its ownership check");
        else
            operations.Should().Contain(expectedOperation);
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
        var users = Substitute.For<IUserServiceRead>();
        users.GetUserInfoAsync(actorId, Arg.Any<CancellationToken>()).Returns(
            UserInfo.Create(new User { Id = actorId }, [], [], [], null, []));
        var reports = Substitute.For<IExpenseReportService>();
        reports.GetAsync(reportId).Returns(new ExpenseReportDto
        {
            Id = reportId, SubmitterUserId = actorId, BudgetCategoryId = Guid.NewGuid(),
            BudgetYearId = Guid.NewGuid(), Status = ExpenseReportStatus.Draft,
            PayeeName = "Submitter", PayeeIban = "", Total = 0,
            CreatedAt = default, UpdatedAt = default, Lines = []
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
            Substitute.For<IBudgetServiceRead>(), Substitute.For<IHoldedFinanceServiceRead>(),
            Substitute.For<IAuthorizationService>(), NullLogger<ExpensesController>.Instance, localizer)
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
