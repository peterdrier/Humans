using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using System.Globalization;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Primitives;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Claims;
using AwesomeAssertions;
using Humans.Base.Constants;
using Humans.Holded.Contracts;
using Humans.Workgroups.Controllers;
using Humans.Workgroups.Domain;
using Humans.Workgroups.Models;
using Humans.Workgroups.Tests.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Humans.Workgroups.Services;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using NSubstitute;
using Xunit;

namespace Humans.Workgroups.Tests.Controllers;

public sealed class WorkgroupsAdminControllerTests : WorkgroupsTestHarness
{
    private readonly ILogger<WorkgroupsAdminController> _logger = Substitute.For<ILogger<WorkgroupsAdminController>>();

    [HumansTheory]
    [InlineData(null)]
    [InlineData("not-a-date")]
    [InlineData("2025-03-01")]
    public async Task RegisterExisting_RequiresExplicitValidRegistrationDate(string? registeredOn)
    {
        var service = Substitute.For<IWorkgroupService>();
        service.RegisterExistingAsync(Arg.Any<Guid>(), Arg.Any<WorkgroupBootstrap>(), Arg.Any<CancellationToken>())
            .Returns(new WorkgroupMutationResult<Guid>(Guid.NewGuid()));
        var controller = MakeAdminController(nameof(WorkgroupsAdminController.RegisterExisting), service: service);
        controller.Url = Substitute.For<IUrlHelper>();
        using var services = new ServiceCollection().AddLogging().AddControllers().Services.BuildServiceProvider();
        controller.HttpContext.RequestServices = services;
        var fields = new Dictionary<string, StringValues>(StringComparer.Ordinal)
        {
            ["Application.Name"] = "Group",
            ["Application.Purpose"] = "Purpose",
            ["Application.Deliverable"] = "Report",
            ["CoordinatorUserId"] = SeedUser("Coordinator").ToString()
        };
        if (registeredOn is not null) fields["RegisteredOn"] = registeredOn;
        var metadata = services.GetRequiredService<IModelMetadataProvider>().GetMetadataForType(typeof(RegisterExistingViewModel));
        var binder = services.GetRequiredService<IModelBinderFactory>().CreateBinder(new ModelBinderFactoryContext { Metadata = metadata });
        var binding = DefaultModelBindingContext.CreateBindingContext(controller.ControllerContext,
            new FormValueProvider(BindingSource.Form, new FormCollection(fields), CultureInfo.InvariantCulture),
            metadata, bindingInfo: null, modelName: "");
        await binder.BindModelAsync(binding);
        var model = (RegisterExistingViewModel)binding.Result.Model!;
        services.GetRequiredService<IObjectModelValidator>().Validate(controller.ControllerContext, null, "", model);
        var result = await controller.RegisterExisting(model, Ct);

        if (!string.Equals(registeredOn, "2025-03-01", StringComparison.Ordinal))
        {
            controller.ModelState[nameof(RegisterExistingViewModel.RegisteredOn)]!.Errors.Should().NotBeEmpty();
            result.Should().BeOfType<ViewResult>().Which.Model.Should().BeSameAs(model);
            await service.DidNotReceiveWithAnyArgs().RegisterExistingAsync(default, default!);
        }
        else
        {
            controller.ModelState.IsValid.Should().BeTrue();
            result.Should().BeOfType<RedirectToActionResult>();
            await service.Received(1).RegisterExistingAsync(Arg.Any<Guid>(),
                Arg.Is<WorkgroupBootstrap>(b => b.RegisteredAt == new LocalDate(2025, 3, 1).AtMidnight().InUtc().ToInstant()),
                Arg.Any<CancellationToken>());
        }
    }

    [HumansTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Disposition_InvalidOrMissingChoice_DoesNotRecordAcceptance(bool malformed, bool missingChoice)
    {
        var group = await SeedWorkgroupAsync();
        var document = await AddDocumentAsync(group.Id, WorkgroupDocumentStatus.Delivered);
        var controller = MakeAdminController(nameof(WorkgroupsAdminController.Disposition));
        if (malformed) controller.ModelState.AddModelError("disposition", "Invalid value.");
        var registrations = new ServiceCollection().AddLogging();
        registrations.AddControllers();
        using var services = registrations.BuildServiceProvider();
        if (missingChoice)
        {
            controller.HttpContext.RequestServices = services;
            var parameter = typeof(WorkgroupsAdminController).GetMethod(nameof(WorkgroupsAdminController.Disposition))!
                .GetParameters().Single(p => p.ParameterType.IsEnum);
            var metadata = ((ModelMetadataProvider)services.GetRequiredService<IModelMetadataProvider>()).GetMetadataForParameter(parameter);
            var descriptor = new ControllerParameterDescriptor
            {
                Name = parameter.Name!,
                ParameterType = parameter.ParameterType,
                ParameterInfo = parameter
            };
            var binder = services.GetRequiredService<IModelBinderFactory>().CreateBinder(new ModelBinderFactoryContext
            {
                Metadata = metadata,
                CacheToken = parameter
            });
            await services.GetRequiredService<ParameterBinder>().BindModelAsync(controller.ControllerContext, binder,
                new FormValueProvider(BindingSource.Form, new FormCollection(new Dictionary<string, StringValues>(StringComparer.Ordinal)), CultureInfo.InvariantCulture),
                descriptor, metadata, value: null);
        }

        var result = await controller.Disposition(document.Id, WorkgroupDisposition.Accepted, "Agreed", Ct);

        if (malformed || missingChoice) result.Should().BeOfType<BadRequestObjectResult>();
        else result.Should().BeOfType<RedirectToActionResult>();
        await using var db = OpenContext();
        var stored = await db.Documents.SingleAsync(d => d.Id == document.Id, Ct);
        stored.Disposition.Should().Be(malformed || missingChoice ? null : WorkgroupDisposition.Accepted);
        if (malformed || missingChoice)
        {
            AuditLog.ReceivedCalls().Should().BeEmpty();
            Notifications.ReceivedCalls().Should().BeEmpty();
            Email.ReceivedCalls().Should().BeEmpty();
        }
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnexpectedDependencyLookup_IsNotNotFound(bool budget)
    {
        var service = Substitute.For<IWorkgroupService>();
        var id = Guid.NewGuid();
        var failure = new KeyNotFoundException("Dependency lookup failed");
        service.SetBudgetAsync(id, Arg.Any<Guid>(), Arg.Any<WorkgroupBudgetSave>(), Ct)
            .Returns(Task.FromException<WorkgroupMutationResult<Humans.Finance.Contracts.HoldedExpenseAccountRef?>>(failure));
        service.RecordDispositionAsync(id, Arg.Any<Guid>(), WorkgroupDisposition.Noted, "Noted", Ct)
            .Returns(Task.FromException<WorkgroupMutationResult>(failure));
        var controller = MakeAdminController("Test", service: service);
        Func<Task<IActionResult>> act = budget
            ? () => controller.Budget(id, new WorkgroupBudgetFormViewModel { HasBudget = false }, "group", Ct)
            : () => controller.Disposition(id, WorkgroupDisposition.Noted, "Noted", Ct);

        (await act.Should().ThrowAsync<KeyNotFoundException>()).Which.Should().BeSameAs(failure);
        controller.TempData.Should().BeEmpty();
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidRegisterOrSettingsForm_LogsRuleWithoutStack(bool settings)
    {
        var action = settings ? nameof(WorkgroupsAdminController.Settings) : nameof(WorkgroupsAdminController.RegisterExisting);
        var controller = MakeAdminController(action);
        object model = settings
            ? new WorkgroupsSettingsViewModel { RootDriveFolderId = " " }
            : new RegisterExistingViewModel
            {
                Application = new WorkgroupFormViewModel { Name = " " },
                CoordinatorUserId = SeedUser("Coordinator"),
                RegisteredOn = Clock.GetCurrentInstant().InUtc().Date
            };
        var result = settings
            ? await controller.Settings((WorkgroupsSettingsViewModel)model, Ct)
            : await controller.RegisterExisting((RegisterExistingViewModel)model, Ct);

        result.Should().BeOfType<ViewResult>().Which.Model.Should().BeSameAs(model);
        controller.ModelState.IsValid.Should().BeFalse();
        (await OpenContext().Workgroups.ToListAsync(Ct)).Should().BeEmpty();
        AssertRuleWarning(settings ? WorkgroupErrorKeys.RootFolderNotConfigured : WorkgroupErrorKeys.NameRequired);
    }

    private void AssertRuleWarning(string key)
    {
        var args = _logger.ReceivedCalls().Should().ContainSingle(call =>
            string.Equals(call.GetMethodInfo().Name, "Log", StringComparison.Ordinal)).Subject.GetArguments();
        args[0].Should().Be(LogLevel.Warning);
        args[3].Should().BeNull();
        args[2]!.ToString().Should().Contain(key);
    }

    [HumansTheory]
    [InlineData(nameof(WorkgroupsAdminController.Refuse))]
    [InlineData(nameof(WorkgroupsAdminController.Withdraw))]
    [InlineData(nameof(WorkgroupsAdminController.Close))]
    public async Task Invalid_reasons_flash_error_and_redirect_to_details(string action)
    {
        var status = string.Equals(action, nameof(WorkgroupsAdminController.Refuse), StringComparison.Ordinal)
            ? WorkgroupStatus.Applied : WorkgroupStatus.Active;
        var workgroup = await SeedWorkgroupAsync(status: status);
        var sut = MakeAdminController(action, errorMessage: "Use 4000 characters or fewer.");
        var reasons = new string('x', 4001);

        var result = action switch
        {
            nameof(WorkgroupsAdminController.Refuse) => await sut.Refuse(workgroup.Id, reasons, workgroup.Slug, Ct),
            nameof(WorkgroupsAdminController.Withdraw) => await sut.Withdraw(workgroup.Id, reasons, workgroup.Slug, Ct),
            _ => await sut.Close(workgroup.Id, reasons, workgroup.Slug, Ct)
        };

        var redirect = result.Should().BeOfType<RedirectToActionResult>().Subject;
        redirect.ControllerName.Should().Be("Workgroups");
        redirect.ActionName.Should().Be("Details");
        redirect.RouteValues!["slug"].Should().Be(workgroup.Id);
        sut.TempData[TempDataKeys.ErrorMessage].Should().Be("Use 4000 characters or fewer.");
        sut.TempData.ContainsKey(WorkgroupsAdminController.DraftKey(workgroup.Id, action)).Should().BeFalse("oversized drafts are not stashed in TempData");
        AssertRuleWarning(WorkgroupErrorKeys.TextTooLong);
        await using var db = OpenContext();
        (await db.Workgroups.FindAsync([workgroup.Id], Ct))!.Status.Should().Be(status);
    }

    [HumansTheory]
    [InlineData(nameof(WorkgroupsAdminController.Refuse))]
    [InlineData(nameof(WorkgroupsAdminController.Withdraw))]
    [InlineData(nameof(WorkgroupsAdminController.Close))]
    public async Task In_bounds_rejected_reasons_are_kept_as_draft(string action)
    {
        var status = string.Equals(action, nameof(WorkgroupsAdminController.Refuse), StringComparison.Ordinal)
            ? WorkgroupStatus.Applied : WorkgroupStatus.Active;
        var workgroup = await SeedWorkgroupAsync(status: status);
        var sut = MakeAdminController(action, errorMessage: "Reasons are required.");
        const string reasons = "   ";

        var result = action switch
        {
            nameof(WorkgroupsAdminController.Refuse) => await sut.Refuse(workgroup.Id, reasons, workgroup.Slug, Ct),
            nameof(WorkgroupsAdminController.Withdraw) => await sut.Withdraw(workgroup.Id, reasons, workgroup.Slug, Ct),
            _ => await sut.Close(workgroup.Id, reasons, workgroup.Slug, Ct)
        };

        result.Should().BeOfType<RedirectToActionResult>();
        sut.TempData[WorkgroupsAdminController.DraftKey(workgroup.Id, action)].Should().Be(reasons);
    }

    [HumansFact]
    public async Task Close_WithReasons_RedirectsToDetails()
    {
        var workgroup = await SeedWorkgroupAsync(status: WorkgroupStatus.Active);
        var sut = MakeAdminController(nameof(WorkgroupsAdminController.Close));

        var result = await sut.Close(workgroup.Id, "Finished", workgroup.Slug, Ct);

        var redirect = result.Should().BeOfType<RedirectToActionResult>().Subject;
        redirect.ControllerName.Should().Be("Workgroups");
        redirect.ActionName.Should().Be("Details");
        await using var db = OpenContext();
        (await db.Workgroups.FindAsync([workgroup.Id], Ct))!.Status.Should().Be(WorkgroupStatus.Dormant);
    }

    [HumansFact]
    public async Task Refuse_WithoutSlug_RedirectsToQueue()
    {
        var workgroup = await SeedWorkgroupAsync(status: WorkgroupStatus.Applied);
        var sut = MakeAdminController(nameof(WorkgroupsAdminController.Refuse));

        var result = await sut.Refuse(workgroup.Id, "No", null, Ct);

        result.Should().BeOfType<RedirectToActionResult>().Which.ActionName.Should().Be(nameof(WorkgroupsAdminController.Index));
    }

    [HumansFact]
    public async Task Budget_ValidForm_SetsBudgetAndRedirectsToTheGroup()
    {
        var workgroup = await SeedWorkgroupAsync(name: "ALM 2027");
        var sut = MakeAdminController(nameof(WorkgroupsAdminController.Budget));

        var result = await sut.Budget(workgroup.Id,
            new WorkgroupBudgetFormViewModel { HasBudget = true, Amount = "900", AccountMode = "create" },
            workgroup.Slug, Ct);

        var redirect = result.Should().BeOfType<RedirectToActionResult>().Subject;
        redirect.ControllerName.Should().Be("Workgroups");
        redirect.ActionName.Should().Be("Details");
        redirect.RouteValues!["slug"].Should().Be(workgroup.Id);
        await using var ctx = OpenContext();
        (await ctx.Workgroups.SingleAsync(w => w.Id == workgroup.Id, Ct)).BudgetAmount.Should().Be(900m);
    }

    [HumansFact]
    public async Task Budget_RuleFailure_RedirectsWithError()
    {
        var workgroup = await SeedWorkgroupAsync(status: WorkgroupStatus.Withdrawn);
        var sut = MakeAdminController(nameof(WorkgroupsAdminController.Budget));

        var result = await sut.Budget(workgroup.Id,
            new WorkgroupBudgetFormViewModel { HasBudget = true, Amount = "10", AccountMode = "create" },
            workgroup.Slug, Ct);

        result.Should().BeOfType<RedirectToActionResult>();
        sut.TempData.Should().ContainKey(TempDataKeys.ErrorMessage);
        AssertRuleWarning(WorkgroupErrorKeys.WrongStatus);
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
            { HasBudget = true, Amount = "500", AccountMode = "link", ExistingAccountNum = 62900007 }
        };

        var result = await sut.RegisterExisting(model, Ct);

        result.Should().BeOfType<RedirectToActionResult>();
        await Finance.Received(1).CreateOrLinkExpenseAccountAsync("Workgroups / Sound", 62900007, Arg.Any<CancellationToken>());
    }

    /// <summary>A Board-actor <see cref="WorkgroupsAdminController"/> wired to an in-memory context,
    /// with a localizer that renders any rule key as <paramref name="errorMessage"/>.</summary>
    private WorkgroupsAdminController MakeAdminController(string actionName, string errorMessage = "Rule failed.", IWorkgroupService? service = null)
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
        return new WorkgroupsAdminController(service ?? NewService(), Users, localizer, Clock,
            _logger)
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
