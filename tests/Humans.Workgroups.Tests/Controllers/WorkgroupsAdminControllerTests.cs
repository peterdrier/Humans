using System.Security.Claims;
using AwesomeAssertions;
using Humans.Base.Constants;
using Humans.Holded.Contracts;
using Humans.Testing;
using Humans.Workgroups.Controllers;
using Humans.Workgroups.Domain;
using Humans.Workgroups.Models;
using Humans.Workgroups.Tests.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using NSubstitute;
using Xunit;

namespace Humans.Workgroups.Tests.Controllers;

public sealed class WorkgroupsAdminControllerTests : WorkgroupsTestHarness
{
    [HumansTheory]
    [InlineData(nameof(WorkgroupsAdminController.Refuse))]
    [InlineData(nameof(WorkgroupsAdminController.Withdraw))]
    [InlineData(nameof(WorkgroupsAdminController.Close))]
    public async Task Invalid_reasons_render_queue_with_original_text_and_error(string action)
    {
        var status = string.Equals(action, nameof(WorkgroupsAdminController.Refuse), StringComparison.Ordinal)
            ? WorkgroupStatus.Applied : WorkgroupStatus.Active;
        var workgroup = await SeedWorkgroupAsync(status: status);
        var sut = MakeAdminController(action, errorMessage: "Use 4000 characters or fewer.");
        var reasons = new string('x', 4001);

        var result = action switch
        {
            nameof(WorkgroupsAdminController.Refuse) => await sut.Refuse(workgroup.Id, reasons, Ct),
            nameof(WorkgroupsAdminController.Withdraw) => await sut.Withdraw(workgroup.Id, reasons, Ct),
            _ => await sut.Close(workgroup.Id, reasons, Ct)
        };

        var view = result.Should().BeOfType<ViewResult>().Subject;
        view.ViewName.Should().Be(nameof(WorkgroupsAdminController.Index));
        view.Model.Should().BeOfType<AdminQueueViewModel>().Subject.Register
            .Should().ContainSingle().Which.Id.Should().Be(workgroup.Id);
        view.ViewData[$"Reasons:{workgroup.Id}:{action}"].Should().Be(reasons);
        sut.ModelState.IsValid.Should().BeFalse();
        sut.ModelState[string.Empty]!.Errors.Should().ContainSingle()
            .Which.ErrorMessage.Should().Contain("4000");
        sut.TempData.Should().BeEmpty("oversized text must not enter the TempData cookie");
        await using var db = OpenContext();
        (await db.Workgroups.FindAsync([workgroup.Id], Ct))!.Status.Should().Be(status);
    }

    [HumansFact]
    public async Task Budget_ValidForm_SetsBudgetAndRedirectsToTheGroup()
    {
        var workgroup = await SeedWorkgroupAsync(name: "ALM 2027");
        var sut = MakeAdminController(nameof(WorkgroupsAdminController.Budget));

        var result = await sut.Budget(workgroup.Id,
            new WorkgroupBudgetFormViewModel { HasBudget = true, Amount = 900m, AccountMode = "create" },
            workgroup.Slug, Ct);

        var redirect = result.Should().BeOfType<RedirectToActionResult>().Subject;
        redirect.ControllerName.Should().Be("Workgroups");
        redirect.ActionName.Should().Be("Details");
        redirect.RouteValues!["slug"].Should().Be(workgroup.Slug);
        await using var ctx = OpenContext();
        (await ctx.Workgroups.SingleAsync(w => w.Id == workgroup.Id, Ct)).BudgetAmount.Should().Be(900m);
    }

    [HumansFact]
    public async Task Budget_RuleFailure_RedirectsWithError()
    {
        var workgroup = await SeedWorkgroupAsync(status: WorkgroupStatus.Withdrawn);
        var sut = MakeAdminController(nameof(WorkgroupsAdminController.Budget));

        var result = await sut.Budget(workgroup.Id,
            new WorkgroupBudgetFormViewModel { HasBudget = true, Amount = 10m, AccountMode = "create" },
            workgroup.Slug, Ct);

        result.Should().BeOfType<RedirectToActionResult>();
        sut.TempData.Should().ContainKey(TempDataKeys.ErrorMessage);
    }

    [HumansFact]
    public async Task RegisterExisting_Get_OffersTheLiveChartForLinking()
    {
        Holded.ListExpenseAccountsAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IReadOnlyList<HoldedExpenseAccountDto>>(
                [new HoldedExpenseAccountDto { Id = "a1", AccountNum = 62900007, Name = "Sound (legacy)" }]));
        var sut = MakeAdminController(nameof(WorkgroupsAdminController.RegisterExisting));

        var result = await sut.RegisterExisting(Ct);

        result.Should().BeOfType<ViewResult>().Subject.Model.Should().BeOfType<RegisterExistingViewModel>()
            .Subject.ExpenseAccounts.Should().ContainSingle().Which.AccountNum.Should().Be(62900007);
    }

    [HumansFact]
    public async Task RegisterExisting_LinkMode_BindsTheChosenAccountInsteadOfCreating()
    {
        var sut = MakeAdminController(nameof(WorkgroupsAdminController.RegisterExisting));
        var model = new RegisterExistingViewModel
        {
            Application = new WorkgroupFormViewModel
            {
                Name = "Sound",
                Purpose = "Purpose",
                Deliverable = "A report",
                DeliverableKind = WorkgroupDeliverableKind.Report,
                Audience = WorkgroupAudience.Board
            },
            CoordinatorUserId = SeedUser("Coordinator"),
            RegisteredOn = new LocalDate(2025, 3, 1),
            Budget = new WorkgroupBudgetFormViewModel
            { HasBudget = true, Amount = 500m, AccountMode = "link", ExistingAccountNum = 62900007 }
        };

        var result = await sut.RegisterExisting(model, Ct);

        result.Should().BeOfType<RedirectToActionResult>();
        await Finance.Received(1).CreateOrLinkExpenseAccountAsync("Workgroups / Sound", 62900007, Arg.Any<CancellationToken>());
    }

    /// <summary>A Board-actor <see cref="WorkgroupsAdminController"/> wired to an in-memory context,
    /// with a localizer that renders any rule key as <paramref name="errorMessage"/>.</summary>
    private WorkgroupsAdminController MakeAdminController(string actionName, string errorMessage = "Rule failed.")
    {
        var actor = SeedUser("Board member");
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, actor.ToString()),
                new Claim(ClaimTypes.Role, RoleNames.Board)], "test"))
        };
        var localizer = Substitute.For<IStringLocalizer<WorkgroupsResource>>();
        localizer[Arg.Any<string>(), Arg.Any<object[]>()]
            .Returns(call => new LocalizedString(call.Arg<string>(), errorMessage));
        return new WorkgroupsAdminController(NewService(), Users, localizer, Clock,
            NullLogger<WorkgroupsAdminController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = http,
                ActionDescriptor = new ControllerActionDescriptor { ActionName = actionName }
            },
            TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>())
        };
    }
}
