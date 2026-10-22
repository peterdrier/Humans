using AwesomeAssertions;
using System.Globalization;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using System.Security.Claims;
using Humans.Budget.Controllers;
using Humans.Budget.Models;
using Humans.Finance.Contracts;
using Humans.Tickets.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Localization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NodaTime.Testing;
using Humans.Teams.Contracts;
using Humans.Base.Enums;
using Humans.Budget.Domain;
using Humans.Budget.Contracts;
using Humans.Budget.Data;
using Humans.Budget.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;
using Xunit;
using BudgetServiceImpl = Humans.Budget.Services.BudgetService;
using Humans.Users.Contracts;

namespace Humans.Budget.Tests.Services;

/// <summary>
/// Owns its fixture rather than deriving from <c>Humans.Application.Tests</c>'
/// <c>ServiceTestHarness</c>: that harness is built around an in-memory
/// <c>UsersDbContext</c> and this test only ever used two of its members — the clock and
/// the Budget section context. Inheriting it would have granted a section test project
/// <c>InternalsVisibleTo</c> on <c>UsersDbContext</c>, which is the boundary the G5 split
/// exists to draw (nobodies-collective/Humans#866).
/// </summary>
public sealed class BudgetServiceTests
{
    private readonly FakeClock Clock = new(Instant.FromUtc(2026, 3, 31, 12, 0));

    private readonly TestDbContextFactory<BudgetDbContext> BudgetDbFactory =
        new(new DbContextOptionsBuilder<BudgetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private readonly BudgetRepository _repository;
    private readonly ITeamService _teamService;
    private readonly IUserService _userService;
    private readonly BudgetServiceImpl _service;
    private readonly Guid _yearId = Guid.NewGuid();

    public BudgetServiceTests()
    {
        _repository = new BudgetRepository(BudgetDbFactory, NullLogger<BudgetRepository>.Instance);
        _teamService = Substitute.For<ITeamService>();
        // Nothing merged into anybody: the substitute's GetUserInfoAsync returns null and
        // the export reads the single id.
        _userService = Substitute.For<IUserService>();

        _service = new BudgetServiceImpl(
            _repository,
            _teamService,
            _userService,
            Clock,
            NullLogger<BudgetServiceImpl>.Instance);
    }

    [HumansTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task YearReads_CancelledRead_AbortsRepositoryQuery(bool active)
    {
        using var request = new CancellationTokenSource();
        await request.CancelAsync();

        var read = () => active
            ? _service.GetActiveYearAsync(request.Token)
            : _service.GetYearByIdAsync(_yearId, request.Token);

        await read.Should().ThrowAsync<OperationCanceledException>();
    }

    [HumansTheory]
    [InlineData("Index")]
    [InlineData("YearDetail")]
    [InlineData("CategoryDetail")]
    [InlineData("AuditLog")]
    [InlineData("Admin")]
    public async Task AdminReads_RequestCancellation_IsNotRenderedAsLoadFailure(string action)
    {
        using var request = new CancellationTokenSource();
        var (controller, budget, _) = CreateAdminController(true);
        controller.HttpContext.RequestAborted = request.Token;
        budget.GetActiveYearAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<BudgetYearDetail?>(new OperationCanceledException(request.Token)));
        budget.GetYearByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<BudgetYearDetail?>(new OperationCanceledException(request.Token)));
        budget.GetCategoryByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<BudgetCategorySnapshot?>(new OperationCanceledException(request.Token)));
        budget.GetAllYearsAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<BudgetYearSummarySnapshot>>(new OperationCanceledException(request.Token)));

        Func<Task<IActionResult>> read = action switch
        {
            "Index" => controller.Index,
            "YearDetail" => () => controller.YearDetail(_yearId),
            "CategoryDetail" => () => controller.CategoryDetail(_yearId),
            "AuditLog" => () => controller.AuditLog(null),
            _ => controller.Admin
        };
        await read.Should().ThrowAsync<OperationCanceledException>();
        controller.TempData.Should().BeEmpty();
        switch (action)
        {
            case "YearDetail": _ = budget.Received(1).GetYearByIdAsync(_yearId, request.Token); break;
            case "CategoryDetail": _ = budget.Received(1).GetCategoryByIdAsync(_yearId, request.Token); break;
            case "Admin": _ = budget.Received(1).GetAllYearsAsync(true, request.Token); break;
            default: _ = budget.Received(1).GetActiveYearAsync(request.Token); break;
        }
    }

    [HumansTheory]
    [InlineData("Index", true)]
    [InlineData("Summary", true)]
    [InlineData("CategoryDetail", true)]
    [InlineData("Index", false)]
    [InlineData("Summary", false)]
    [InlineData("CategoryDetail", false)]
    public async Task MemberReads_RequestCancellation_IsNotRenderedAsLoadFailure(string action, bool cancelActorRead)
    {
        using var request = new CancellationTokenSource();
        var (admin, budget, viewerId) = CreateAdminController(true);
        admin.HttpContext.RequestAborted = request.Token;
        var users = Substitute.For<IUserService>();
        users.GetUserInfoAsync(viewerId, Arg.Any<CancellationToken>()).Returns(_ => cancelActorRead
            ? new ValueTask<UserInfo?>(Task.FromException<UserInfo?>(new OperationCanceledException(request.Token)))
            : new ValueTask<UserInfo?>(UserInfo.Create(new User { Id = viewerId }, [], [], [], null, [])));
        var auth = Substitute.For<IAuthorizationService>();
        auth.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(AuthorizationResult.Success());
        budget.GetCoordinatorBudgetViewDataAsync(viewerId, true, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<CoordinatorBudgetViewData>(new OperationCanceledException(request.Token)));
        budget.GetActiveYearAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<BudgetYearDetail?>(new OperationCanceledException(request.Token)));
        budget.GetCoordinatorCategoryDetailViewDataAsync(_yearId, viewerId, true, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<CoordinatorCategoryDetailViewData>(new OperationCanceledException(request.Token)));
        var controller = new BudgetController(budget, auth, users,
            Substitute.For<IStringLocalizer<BudgetResource>>(), NullLogger<BudgetController>.Instance)
        {
            ControllerContext = admin.ControllerContext,
            TempData = admin.TempData,
            Url = admin.Url
        };
        Func<Task<IActionResult>> read = action switch
        {
            "Index" => controller.Index,
            "Summary" => controller.Summary,
            _ => () => controller.CategoryDetail(_yearId)
        };

        await read.Should().ThrowAsync<OperationCanceledException>();
        controller.TempData.Should().BeEmpty();
        _ = users.Received(1).GetUserInfoAsync(viewerId, request.Token);
        if (!cancelActorRead)
        {
            switch (action)
            {
                case "Index": _ = budget.Received(1).GetCoordinatorBudgetViewDataAsync(viewerId, true, request.Token); break;
                case "Summary": _ = budget.Received(1).GetActiveYearAsync(request.Token); break;
                default: _ = budget.Received(1).GetCoordinatorCategoryDetailViewDataAsync(_yearId, viewerId, true, request.Token); break;
            }
        }
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CoordinatorViews_CancelledRead_AbortsRepositoryQuery(bool category)
    {
        using var request = new CancellationTokenSource();
        await request.CancelAsync();
        Func<Task> query = category
            ? () => _service.GetCoordinatorCategoryDetailViewDataAsync(_yearId, Guid.NewGuid(), true, request.Token)
            : () => _service.GetCoordinatorBudgetViewDataAsync(Guid.NewGuid(), true, request.Token);

        await query.Should().ThrowAsync<OperationCanceledException>();
    }

    [HumansTheory]
    [InlineData("Category")]
    [InlineData("Years")]
    [InlineData("Audit")]
    public async Task AdditionalReads_CancelledRead_AbortsRepositoryQuery(string read)
    {
        using var request = new CancellationTokenSource();
        await request.CancelAsync();
        Func<Task> query = read switch
        {
            "Category" => () => _service.GetCategoryByIdAsync(_yearId, request.Token),
            "Years" => () => _service.GetAllYearsAsync(ct: request.Token),
            _ => () => _service.GetAuditLogAsync(_yearId, request.Token)
        };

        await query.Should().ThrowAsync<OperationCanceledException>();
    }

    [HumansTheory]
    [InlineData(false, "isRestricted", true)]
    [InlineData(true, "isRestricted", true)]
    [InlineData(true, "sortOrder", true)]
    [InlineData(false, "isRestricted", false)]
    [InlineData(true, "isRestricted", false)]
    [InlineData(true, "sortOrder", false)]
    [InlineData(false, null, true)]
    [InlineData(true, null, true)]
    public async Task GroupForm_InvalidBinding_DoesNotMutate(bool update, string? invalidField, bool viewerExists)
    {
        var (controller, budget, viewerId) = CreateAdminController(viewerExists);
        if (invalidField is not null) controller.ModelState.AddModelError(invalidField, "Invalid value.");

        var result = update
            ? await controller.UpdateGroup(_yearId, "Existing", 0, false)
            : await controller.CreateGroup(_yearId, "New", false);

        if (!viewerExists) Assert.IsType<NotFoundResult>(result);
        else if (invalidField is not null) Assert.IsType<BadRequestObjectResult>(result);
        else Assert.IsType<RedirectToActionResult>(result);
        if (!viewerExists || invalidField is not null) Assert.Empty(budget.ReceivedCalls());
        else if (update) await budget.Received(1).UpdateGroupAsync(_yearId, "Existing", 0, false, viewerId);
        else await budget.Received(1).CreateGroupAsync(_yearId, "New", false, viewerId);
    }

    [HumansTheory]
    [InlineData(false, null, true)]
    [InlineData(true, null, true)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    [InlineData(false, null, false)]
    [InlineData(true, null, false)]
    public async Task Group_forms_require_explicit_restriction_before_exposing_details(bool update, bool? restriction, bool viewerExists)
    {
        var category = await SeedCategoryAsync(isRestricted: true);
        var group = category.BudgetGroup!;
        var (controller, _, _) = CreateAdminController(viewerExists, _service);
        if (restriction is null)
            await BindMissingParameterAsync(controller,
                update ? nameof(BudgetAdminController.UpdateGroup) : nameof(BudgetAdminController.CreateGroup), "isRestricted");

        var result = update
            ? await controller.UpdateGroup(group.Id, "Changed group", 0, restriction ?? false)
            : await controller.CreateGroup(group.BudgetYearId, "New group", restriction ?? false);

        var refused = restriction is null || !viewerExists;
        if (!viewerExists) result.Should().BeOfType<NotFoundResult>();
        else if (refused)
        {
            result.Should().BeOfType<BadRequestObjectResult>();
            controller.ModelState["isRestricted"]!.Errors.Should().ContainSingle();
        }
        else result.Should().BeOfType<RedirectToActionResult>();
        await using var db = await BudgetDbFactory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        var groups = await db.BudgetGroups.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);
        groups.Should().HaveCount(update || refused ? 1 : 2);
        var saved = groups.Single(g => g.Id == group.Id);
        saved.IsRestricted.Should().Be(!update || refused || restriction == true);
        if (update)
        {
            saved.Name.Should().Be(refused ? "Departments" : "Changed group");
            var coordinator = Guid.NewGuid();
            _teamService.GetEffectiveBudgetCoordinatorTeamIdsAsync(coordinator, Arg.Any<CancellationToken>()).Returns([Guid.NewGuid()]);
            _teamService.GetTeamsAsync(Arg.Any<CancellationToken>()).Returns(new Dictionary<Guid, TeamInfo>());
            (await _service.GetCoordinatorCategoryDetailViewDataAsync(category.Id, coordinator, false))
                .ShouldForbid.Should().Be(refused || restriction == true);
        }
        else if (!refused) groups.Single(g => g.Id != group.Id).IsRestricted.Should().Be(restriction!.Value);
        if (refused) (await db.BudgetAuditLogs.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
    }

    [HumansTheory]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(false, true, false)]
    public async Task YearStatus_InvalidOrMissingBinding_DoesNotResetActiveYear(bool malformed, bool missingStatus, bool viewerExists)
    {
        _teamService.GetTeamsAsync(Arg.Any<CancellationToken>()).Returns(new Dictionary<Guid, TeamInfo>());
        var year = await _service.CreateYearAsync("2026", "Budget 2026", Guid.NewGuid());
        await _service.UpdateYearStatusAsync(year.Id, BudgetYearStatus.Active, Guid.NewGuid());
        var (controller, _, _) = CreateAdminController(viewerExists, _service);
        if (malformed) controller.ModelState.AddModelError("status", "Invalid value.");
        if (missingStatus)
            await BindMissingParameterAsync(controller, nameof(BudgetAdminController.UpdateYearStatus), "status");

        var result = await controller.UpdateYearStatus(year.Id, BudgetYearStatus.Draft);
        var refused = malformed || missingStatus || !viewerExists;

        if (!viewerExists) result.Should().BeOfType<NotFoundResult>();
        else if (refused) result.Should().BeOfType<BadRequestObjectResult>();
        else result.Should().BeOfType<RedirectToActionResult>();
        await using var db = await BudgetDbFactory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        (await db.BudgetYears.SingleAsync(y => y.Id == year.Id, TestContext.Current.CancellationToken))
            .Status.Should().Be(refused ? BudgetYearStatus.Active : BudgetYearStatus.Draft);
        (await db.BudgetAuditLogs.CountAsync(TestContext.Current.CancellationToken)).Should().Be(refused ? 2 : 3);
    }

    [HumansTheory]
    [InlineData("DailySalesRate", true)]
    [InlineData("VatRate", true)]
    [InlineData("StartDate", true)]
    [InlineData("DailySalesRate", false)]
    [InlineData(null, true)]
    public async Task ProjectionForm_InvalidBinding_DoesNotSaveOrRegenerate(string? invalidField, bool viewerExists)
    {
        var (controller, budget, viewerId) = CreateAdminController(viewerExists);
        if (invalidField is not null)
            controller.ModelState.AddModelError(invalidField, "Invalid value.");

        var result = await controller.UpdateTicketingProjection(_yearId,
            new TicketingProjectionUpdateForm { BudgetYearId = _yearId });

        if (!viewerExists) Assert.IsType<NotFoundResult>(result);
        else if (invalidField is not null) Assert.IsType<BadRequestObjectResult>(result);
        else Assert.IsType<RedirectToActionResult>(result);
        if (!viewerExists || invalidField is not null) Assert.Empty(budget.ReceivedCalls());
        else
        {
            await budget.Received(1).UpdateTicketingProjectionAsync(_yearId, null, null,
                0, 0m, 0m, 0, 0m, 0m, 0m, viewerId);
            await budget.Received(1).RefreshTicketingProjectionsAsync(_yearId, viewerId, Arg.Any<CancellationToken>());
        }
    }

    [HumansTheory]
    [InlineData("InitialSalesCount", false, true)]
    [InlineData("DailySalesRate", false, true)]
    [InlineData("AverageTicketPrice", false, true)]
    [InlineData("VatRate", false, true)]
    [InlineData("StripeFeePercent", false, true)]
    [InlineData("StripeFeeFixed", false, true)]
    [InlineData("TicketTailorFeePercent", false, true)]
    [InlineData("", false, true)]
    [InlineData("", true, true)]
    [InlineData("VatRate", false, false)]
    public async Task Projection_form_requires_explicit_financial_assumptions(string omitted, bool zero, bool viewerExists)
    {
        var (controller, budget, viewerId) = CreateAdminController(viewerExists);
        var registrations = new ServiceCollection().AddLogging();
        registrations.AddControllers();
        using var services = registrations.BuildServiceProvider();
        controller.HttpContext.RequestServices = services;
        var fields = new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase)
        {
            ["budgetYearId"] = _yearId.ToString(),
            ["initialSalesCount"] = zero ? "0" : "123",
            ["dailySalesRate"] = zero ? "0" : "5",
            ["averageTicketPrice"] = zero ? "0" : "120",
            ["vatRate"] = zero ? "0" : "21",
            ["stripeFeePercent"] = zero ? "0" : "1.5",
            ["stripeFeeFixed"] = zero ? "0" : "0.3",
            ["ticketTailorFeePercent"] = zero ? "0" : "0.5"
        };
        if (omitted.Length > 0) fields.Remove(omitted);
        var metadata = services.GetRequiredService<IModelMetadataProvider>().GetMetadataForType(typeof(TicketingProjectionUpdateForm));
        var binder = services.GetRequiredService<IModelBinderFactory>().CreateBinder(new ModelBinderFactoryContext
        {
            Metadata = metadata,
            CacheToken = typeof(TicketingProjectionUpdateForm)
        });
        var binding = DefaultModelBindingContext.CreateBindingContext(controller.ControllerContext,
            new FormValueProvider(BindingSource.Form, new FormCollection(fields), CultureInfo.InvariantCulture),
            metadata, bindingInfo: null, modelName: "");
        await binder.BindModelAsync(binding);
        var model = (TicketingProjectionUpdateForm)binding.Result.Model!;
        services.GetRequiredService<IObjectModelValidator>().Validate(controller.ControllerContext, binding.ValidationState, "", model);

        var result = await controller.UpdateTicketingProjection(_yearId, model);

        if (!viewerExists) result.Should().BeOfType<NotFoundResult>();
        else if (omitted.Length > 0)
        {
            result.Should().BeOfType<BadRequestObjectResult>();
            controller.ModelState[omitted]!.Errors.Should().ContainSingle();
        }
        else
        {
            result.Should().BeOfType<RedirectToActionResult>();
            await budget.Received(1).UpdateTicketingProjectionAsync(_yearId, null, null,
                zero ? 0 : 123, zero ? 0m : 5m, zero ? 0m : 120m, zero ? 0 : 21,
                zero ? 0m : 1.5m, zero ? 0m : 0.3m, zero ? 0m : 0.5m, viewerId);
            await budget.Received(1).RefreshTicketingProjectionsAsync(_yearId, viewerId, Arg.Any<CancellationToken>());
        }
        if (!viewerExists || omitted.Length > 0) budget.ReceivedCalls().Should().BeEmpty();
    }

    [HumansTheory]
    [InlineData("missingYear")]
    [InlineData("foreignYear")]
    [InlineData("missingGroup")]
    [InlineData("valid")]
    public async Task Projection_update_validates_group_year_before_parameters_or_forecasts(string scenario)
    {
        var seeded = await SeedTicketingYearAsync();
        var other = await SeedCategoryAsync(isTicketingGroup: true);
        var valid = string.Equals(scenario, "valid", StringComparison.Ordinal);
        var yearId = string.Equals(scenario, "missingYear", StringComparison.Ordinal) ? Guid.Empty
            : string.Equals(scenario, "foreignYear", StringComparison.Ordinal) ? other.BudgetGroup!.BudgetYearId : _yearId;
        var groupId = string.Equals(scenario, "missingGroup", StringComparison.Ordinal) ? Guid.NewGuid() : seeded.GroupId;
        var (controller, _, _) = CreateAdminController(true, _service);

        var result = await controller.UpdateTicketingProjection(groupId, new TicketingProjectionUpdateForm
        {
            BudgetYearId = yearId,
            StartDate = new DateTime(2026, 4, 1),
            EventDate = new DateTime(2026, 4, 7),
            InitialSalesCount = 10,
            DailySalesRate = 5m,
            AverageTicketPrice = 120m,
            VatRate = 21,
            StripeFeePercent = 1.5m,
            StripeFeeFixed = 0.3m,
            TicketTailorFeePercent = 0.5m
        });

        await using var db = await BudgetDbFactory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        var saved = await db.TicketingProjections.AsNoTracking().SingleAsync(p => p.Id == seeded.ProjectionId, TestContext.Current.CancellationToken);
        saved.InitialSalesCount.Should().Be(valid ? 10 : 0);
        if (!valid)
        {
            (await db.BudgetAuditLogs.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
            result.Should().BeOfType<NotFoundResult>();
            controller.TempData.ContainsKey("SuccessMessage").Should().BeFalse();
        }
        else
        {
            result.Should().BeOfType<RedirectToActionResult>();
            saved.DailySalesRate.Should().Be(5m);
            saved.AverageTicketPrice.Should().Be(120m);
            controller.TempData.ContainsKey("SuccessMessage").Should().BeTrue();
        }
    }

    private static async Task BindMissingParameterAsync(Controller controller, string actionName, string field)
    {
        var registrations = new ServiceCollection().AddLogging();
        registrations.AddControllers();
        using var services = registrations.BuildServiceProvider();
        controller.HttpContext.RequestServices = services;
        var parameter = controller.GetType().GetMethod(actionName)!.GetParameters()
            .Single(p => string.Equals(p.Name, field, StringComparison.Ordinal));
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

    private (BudgetAdminController Controller, IBudgetManagement Budget, Guid ViewerId) CreateAdminController(bool viewerExists, IBudgetManagement? budgetOverride = null)
    {
        var viewerId = Guid.NewGuid();
        var budget = Substitute.For<IBudgetManagement>();
        budget.GetYearByIdAsync(_yearId, Arg.Any<CancellationToken>()).Returns(
            new BudgetYearDetail(_yearId, "2026", "Budget", BudgetYearStatus.Active, false,
                [new BudgetGroupDetail(_yearId, _yearId, "Ticketing", 0, false, false, true, null, [])]));
        var users = Substitute.For<IUserService>();
        if (viewerExists)
            users.GetUserInfoAsync(viewerId, Arg.Any<CancellationToken>()).Returns(
                UserInfo.Create(new User { Id = viewerId }, [], [], [], null, []));
        var tickets = Substitute.For<ITicketService>();
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, viewerId.ToString())], "test"))
        };
        var controller = new BudgetAdminController(budgetOverride ?? budget, _teamService,
            new TicketingBudgetService(tickets, budgetOverride ?? budget, Clock, NullLogger<TicketingBudgetService>.Instance),
            Clock, users, Substitute.For<IHoldedFinanceService>(),
            NullLogger<BudgetAdminController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
            TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>()),
            Url = Substitute.For<IUrlHelper>()
        };
        return (controller, budget, viewerId);
    }

    [HumansTheory]
    [InlineData(0, "amount", true, false)]
    [InlineData(0, "amount", false, false)]
    [InlineData(0, null, true, false)]
    [InlineData(1, "amount", true, false)]
    [InlineData(1, "amount", false, false)]
    [InlineData(1, null, true, false)]
    [InlineData(2, "amount", true, false)]
    [InlineData(2, "amount", false, false)]
    [InlineData(2, null, true, false)]
    [InlineData(3, "amount", true, false)]
    [InlineData(3, "amount", false, false)]
    [InlineData(3, null, true, false)]
    [InlineData(0, "amount", true, true)]
    [InlineData(0, "amount", false, true)]
    [InlineData(1, "amount", true, true)]
    [InlineData(1, "amount", false, true)]
    [InlineData(2, "amount", true, true)]
    [InlineData(2, "amount", false, true)]
    [InlineData(3, "amount", true, true)]
    [InlineData(3, "amount", false, true)]
    [InlineData(0, "expenditureType", true, true)]
    [InlineData(0, "expenditureType", false, true)]
    [InlineData(1, "expenditureType", true, true)]
    [InlineData(1, "expenditureType", false, true)]
    [InlineData(2, "vatRate", true, true)]
    [InlineData(2, "vatRate", false, true)]
    [InlineData(3, "vatRate", true, true)]
    [InlineData(3, "vatRate", false, true)]
    public async Task FinancialForm_InvalidOrMissingBinding_DoesNotMutate(int action, string? invalidField, bool viewerExists, bool missingValue)
    {
        var (controller, budget, viewerId) = CreateAdminController(viewerExists);
        if (missingValue)
        {
            var actionName = action switch
            {
                0 => nameof(BudgetAdminController.CreateCategory),
                1 => nameof(BudgetAdminController.UpdateCategory),
                2 => nameof(BudgetAdminController.CreateLineItem),
                _ => nameof(BudgetAdminController.UpdateLineItem)
            };
            await BindMissingParameterAsync(controller, actionName,
                invalidField is "expenditureType" or "vatRate" ? invalidField : action < 2 ? "allocatedAmount" : "amount");
        }
        else if (invalidField is not null)
            controller.ModelState.AddModelError(action < 2 ? "allocatedAmount" : invalidField, "Invalid number.");

        var result = action switch
        {
            0 => await controller.CreateCategory(_yearId, "Category", 0m, default, null, _yearId),
            1 => await controller.UpdateCategory(_yearId, "Category", 0m, default),
            2 => await controller.CreateLineItem(_yearId, "Item", 0m, null, null, null, 0),
            _ => await controller.UpdateLineItem(_yearId, "Item", 0m, null, null, null, 0, _yearId)
        };
        if (!viewerExists) Assert.IsType<NotFoundResult>(result);
        else if (invalidField is not null) Assert.IsType<BadRequestObjectResult>(result);
        else Assert.IsType<RedirectToActionResult>(result);
        if (!viewerExists || invalidField is not null) Assert.Empty(budget.ReceivedCalls());
        else switch (action)
            {
                case 0: await budget.Received(1).CreateCategoryAsync(_yearId, "Category", 0m, default, null, viewerId); break;
                case 1: await budget.Received(1).UpdateCategoryAsync(_yearId, "Category", 0m, default, viewerId); break;
                case 2: await budget.Received(1).CreateLineItemAsync(_yearId, "Item", 0m, null, null, null, 0, viewerId); break;
                default: await budget.Received(1).UpdateLineItemAsync(_yearId, "Item", 0m, null, null, null, 0, viewerId); break;
            }
    }

    [HumansTheory]
    [InlineData(false, true, true, false, "amount")]
    [InlineData(true, true, true, false, "amount")]
    [InlineData(false, true, false, false, "amount")]
    [InlineData(true, true, false, false, "amount")]
    [InlineData(false, false, true, false, "amount")]
    [InlineData(true, false, true, false, "amount")]
    [InlineData(false, false, true, true, "amount")]
    [InlineData(true, false, true, true, "amount")]
    [InlineData(false, false, false, true, "amount")]
    [InlineData(true, false, false, true, "amount")]
    [InlineData(false, false, false, true, "vatRate")]
    [InlineData(false, false, true, true, "vatRate")]
    [InlineData(true, false, false, true, "vatRate")]
    [InlineData(true, false, true, true, "vatRate")]
    public async Task CoordinatorLineForm_ValidatesAfterAuthorization(bool update, bool malformed, bool allowed, bool missingValue, string missingField)
    {
        var (admin, budget, viewerId) = CreateAdminController(true);
        var users = Substitute.For<IUserService>();
        users.GetUserInfoAsync(viewerId, Arg.Any<CancellationToken>()).Returns(
            UserInfo.Create(new User { Id = viewerId }, [], [], [], null, []));
        var category = new BudgetCategorySnapshot(_yearId, _yearId, "Category", 0m, default, null, 0, null, []);
        budget.GetCategoryByIdAsync(_yearId).Returns(category);
        budget.GetLineItemByIdAsync(_yearId).Returns(new BudgetLineItemSnapshot(
            _yearId, _yearId, "Item", 0m, null, null, null, 0, false, false, 0));
        var auth = Substitute.For<IAuthorizationService>();
        auth.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), category, Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(allowed ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        var localizer = Substitute.For<IStringLocalizer<BudgetResource>>();
        localizer[Arg.Any<string>()].Returns(call => new LocalizedString(call.Arg<string>(), call.Arg<string>()));
        localizer[Arg.Any<string>(), Arg.Any<object[]>()].Returns(call => new LocalizedString(call.Arg<string>(), call.Arg<string>()));
        var controller = new BudgetController(budget, auth, users, localizer, NullLogger<BudgetController>.Instance)
        {
            ControllerContext = admin.ControllerContext,
            TempData = admin.TempData,
            Url = admin.Url
        };
        if (missingValue)
            await BindMissingParameterAsync(controller,
                update ? nameof(BudgetController.UpdateLineItem) : nameof(BudgetController.CreateLineItem), missingField);
        else if (malformed) controller.ModelState.AddModelError("amount", "Invalid number.");
        var invalid = malformed || missingValue;
        budget.ClearReceivedCalls();
        var result = update
            ? await controller.UpdateLineItem(_yearId, "Item", 0m, null, null, null, 0)
            : await controller.CreateLineItem(_yearId, "Item", 0m, null, null, null, 0);
        if (allowed && invalid) Assert.IsType<BadRequestObjectResult>(result);
        else Assert.IsType<RedirectToActionResult>(result);
        if (allowed && !invalid)
        {
            if (update) await budget.Received(1).UpdateLineItemAsync(_yearId, "Item", 0m, null, null, null, 0, viewerId);
            else await budget.Received(1).CreateLineItemAsync(_yearId, "Item", 0m, null, null, null, 0, viewerId);
        }
        else
        {
            await budget.DidNotReceiveWithAnyArgs().CreateLineItemAsync(default, default!, default, default, default, default, default, default);
            await budget.DidNotReceiveWithAnyArgs().UpdateLineItemAsync(default, default!, default, default, default, default, default, default);
        }
    }

    // ─── VAT rate validation ─────────────────────────────────────────────────

    [HumansTheory]
    [InlineData(-1)]
    [InlineData(22)]
    public async Task CreateLineItemAsync_rejects_vat_rates_outside_0_to_21(int vatRate)
    {
        var category = await SeedCategoryAsync();

        var act = () => _service.CreateLineItemAsync(
            category.Id,
            "Test line item",
            100m,
            null,
            null,
            null,
            vatRate,
            Guid.NewGuid());

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>()
            .WithMessage("*between 0 and 21*");
    }

    [HumansTheory]
    [InlineData(600)]
    [InlineData(2000)]
    public async Task Line_note_audit_preserves_valid_values_within_its_storage_columns(int noteLength)
    {
        var category = await SeedCategoryAsync();
        var actor = Guid.NewGuid();
        var before = new string('x', noteLength);
        var after = new string('y', noteLength);
        var line = await _service.CreateLineItemAsync(category.Id, "Item", 100m,
            null, before, null, 0, actor);

        await _service.UpdateLineItemAsync(line.Id, "Item", 100m, null, after, null, 0, actor);

        await using var ctx = await BudgetDbFactory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        var audits = await ctx.BudgetAuditLogs.Where(a => a.EntityId == line.Id)
            .ToListAsync(TestContext.Current.CancellationToken);
        var audit = audits.Single(a => string.Equals(a.FieldName, nameof(BudgetLineItem.Notes), StringComparison.Ordinal));
        audit.OldValue.Should().Be(before);
        audit.NewValue.Should().Be(after);
        audit.Description.Should().Contain(before).And.Contain(after);
        (await ctx.BudgetLineItems.SingleAsync(l => l.Id == line.Id, TestContext.Current.CancellationToken))
            .Notes.Should().Be(after);

        // InMemory does not enforce varchar bounds; the produced audit must also fit its mapped columns.
        var mapping = ctx.Model.FindEntityType(typeof(BudgetAuditLog))!;
        audit.OldValue!.Length.Should().BeLessThanOrEqualTo(mapping.FindProperty(nameof(BudgetAuditLog.OldValue))!.GetMaxLength() ?? int.MaxValue);
        audit.NewValue!.Length.Should().BeLessThanOrEqualTo(mapping.FindProperty(nameof(BudgetAuditLog.NewValue))!.GetMaxLength() ?? int.MaxValue);
        audit.Description.Length.Should().BeLessThanOrEqualTo(mapping.FindProperty(nameof(BudgetAuditLog.Description))!.GetMaxLength() ?? int.MaxValue);
    }

    [HumansFact]
    public async Task CreateLineItemAsync_PersistsLineItem()
    {
        var category = await SeedCategoryAsync();

        var created = await _service.CreateLineItemAsync(
            category.Id,
            "Test line item",
            100m,
            null,
            null,
            null,
            0,
            Guid.NewGuid());

        var persisted = await _service.GetLineItemByIdAsync(created.Id);
        persisted.Should().NotBeNull();
        persisted.Description.Should().Be("Test line item");
        persisted.Amount.Should().Be(100m);
    }

    [HumansTheory]
    [InlineData(-1)]
    [InlineData(22)]
    public async Task UpdateLineItemAsync_rejects_vat_rates_outside_0_to_21(int vatRate)
    {
        var category = await SeedCategoryAsync();
        var lineItem = new BudgetLineItem
        {
            Id = Guid.NewGuid(),
            BudgetCategoryId = category.Id,
            Description = "Existing",
            Amount = 100m,
            VatRate = 0
        };

        await using (var ctx = await BudgetDbFactory.CreateDbContextAsync(TestContext.Current.CancellationToken))
        {
            ctx.BudgetLineItems.Add(lineItem);
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var act = () => _service.UpdateLineItemAsync(
            lineItem.Id,
            "Existing",
            100m,
            null,
            null,
            null,
            vatRate,
            Guid.NewGuid());

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>()
            .WithMessage("*between 0 and 21*");
    }

    [HumansTheory]
    [InlineData(-1)]
    [InlineData(22)]
    public async Task UpdateTicketingProjectionAsync_rejects_vat_rates_outside_0_to_21(int vatRate)
    {
        var act = () => _service.UpdateTicketingProjectionAsync(
            Guid.NewGuid(), null, null, 0, 0m, 0m, vatRate, 0m, 0m, 0m, Guid.NewGuid());

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>()
            .WithMessage("*between 0 and 21*");
    }

    // ─── CreateYearAsync with scaffold ──────────────────────────────────────

    [HumansFact]
    public async Task UpdateLineItemAsync_PersistsChanges()
    {
        var category = await SeedCategoryAsync();
        var lineItem = new BudgetLineItem
        {
            Id = Guid.NewGuid(),
            BudgetCategoryId = category.Id,
            Description = "Existing",
            Amount = 100m,
            VatRate = 0
        };
        await using (var ctx = await BudgetDbFactory.CreateDbContextAsync(TestContext.Current.CancellationToken))
        {
            ctx.BudgetLineItems.Add(lineItem);
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await _service.UpdateLineItemAsync(
            lineItem.Id,
            "Updated",
            150m,
            null,
            null,
            null,
            0,
            Guid.NewGuid());

        var persisted = await _service.GetLineItemByIdAsync(lineItem.Id);
        persisted.Should().NotBeNull();
        persisted.Description.Should().Be("Updated");
        persisted.Amount.Should().Be(150m);
    }

    [HumansFact]
    public async Task GetCoordinatorBudgetViewDataAsync_RedirectsNonCoordinatorNonFinanceUser()
    {
        var result = await _service.GetCoordinatorBudgetViewDataAsync(Guid.NewGuid(), isFinanceAdmin: false);

        result.ShouldRedirectToSummary.Should().BeTrue();
        result.Year.Should().BeNull();
    }

    [HumansFact]
    public async Task GetCoordinatorBudgetViewDataAsync_LoadsActiveYearForFinanceAdmin()
    {
        _teamService.GetTeamsAsync(Arg.Any<CancellationToken>()).Returns(
            new Dictionary<Guid, TeamInfo>());
        var year = await _service.CreateYearAsync("2026", "Budget 2026", Guid.NewGuid());
        await _service.UpdateYearStatusAsync(year.Id, BudgetYearStatus.Active, Guid.NewGuid());

        var result = await _service.GetCoordinatorBudgetViewDataAsync(Guid.NewGuid(), isFinanceAdmin: true);

        result.ShouldRedirectToSummary.Should().BeFalse();
        result.Year!.Id.Should().Be(year.Id);
        result.IsFinanceAdmin.Should().BeTrue();
    }

    [HumansFact]
    public async Task GetCoordinatorCategoryDetailViewDataAsync_ReturnsCategoryAndTeamsForFinanceAdmin()
    {
        var category = await SeedCategoryAsync();
        var teamId = Guid.NewGuid();
        var teamInfo = new TeamInfo(
            teamId, "Kitchen", null, "kitchen",
            IsActive: true, IsSystemTeam: false, SystemTeamType: SystemTeamType.None,
            RequiresApproval: false, IsPublicPage: false, IsHidden: false,
            IsPromotedToDirectory: false, CreatedAt: Instant.MinValue,
            Members: []);
        _teamService.GetTeamsAsync(Arg.Any<CancellationToken>()).Returns(
            new Dictionary<Guid, TeamInfo> { [teamId] = teamInfo });

        var result = await _service.GetCoordinatorCategoryDetailViewDataAsync(category.Id, Guid.NewGuid(), isFinanceAdmin: true);

        result.ShouldForbid.Should().BeFalse();
        result.Category!.Id.Should().Be(category.Id);
        result.Teams.Should().HaveCount(1);
        result.Teams[0].Id.Should().Be(teamId);
        result.Teams[0].Name.Should().Be("Kitchen");
    }

    [HumansFact]
    public async Task GetCoordinatorCategoryDetailViewDataAsync_ForbidsNonFinanceNonCoordinator()
    {
        var category = await SeedCategoryAsync();

        var result = await _service.GetCoordinatorCategoryDetailViewDataAsync(category.Id, Guid.NewGuid(), isFinanceAdmin: false);

        result.ShouldForbid.Should().BeTrue();
        result.Category!.Id.Should().Be(category.Id);
        result.Teams.Should().BeEmpty();
    }

    [HumansTheory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task GetCoordinatorCategoryDetailViewDataAsync_ForbidsRestrictedAndTicketingForCoordinator(
        bool isRestricted, bool isTicketingGroup)
    {
        var category = await SeedCategoryAsync(isRestricted, isTicketingGroup);
        var coordinatorId = Guid.NewGuid();
        var deptId = Guid.NewGuid();
        _teamService.GetEffectiveBudgetCoordinatorTeamIdsAsync(coordinatorId, Arg.Any<CancellationToken>())
            .Returns(new[] { deptId });
        _teamService.GetTeamsAsync(Arg.Any<CancellationToken>()).Returns(new Dictionary<Guid, TeamInfo>());

        var denied = await _service.GetCoordinatorCategoryDetailViewDataAsync(category.Id, coordinatorId, isFinanceAdmin: false);
        var allowed = await _service.GetCoordinatorCategoryDetailViewDataAsync(category.Id, coordinatorId, isFinanceAdmin: true);

        denied.ShouldForbid.Should().BeTrue();
        denied.Teams.Should().BeEmpty();
        allowed.ShouldForbid.Should().BeFalse();
    }

    [HumansFact]
    public async Task GetEffectiveCoordinatorTeamIdsAsync_UsesTheTeamsOwnedScope()
    {
        var userId = Guid.NewGuid();
        using var request = new CancellationTokenSource();
        Guid[] expected = [Guid.NewGuid(), Guid.NewGuid()];
        _teamService.GetEffectiveBudgetCoordinatorTeamIdsAsync(userId, Arg.Any<CancellationToken>())
            .Returns(expected);

        var result = await _service.GetEffectiveCoordinatorTeamIdsAsync(userId, request.Token);

        result.Should().BeEquivalentTo(expected);
        await _teamService.Received(1).GetEffectiveBudgetCoordinatorTeamIdsAsync(userId, request.Token);
        await _teamService.DidNotReceive().GetTeamsAsync(Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task CreateYearAsync_seeds_department_and_ticketing_groups_atomically()
    {
        var kitchenId = Guid.NewGuid();
        var siteOpsId = Guid.NewGuid();
        var teams = new Dictionary<Guid, TeamInfo>
        {
            [kitchenId] = new(
                kitchenId, "Kitchen", null, "kitchen",
                IsActive: true, IsSystemTeam: false, SystemTeamType: SystemTeamType.None,
                RequiresApproval: false, IsPublicPage: false, IsHidden: false,
                IsPromotedToDirectory: false, CreatedAt: Instant.MinValue,
                Members: [],
                HasBudget: true),
            [siteOpsId] = new(
                siteOpsId, "Site Ops", null, "site-ops",
                IsActive: true, IsSystemTeam: false, SystemTeamType: SystemTeamType.None,
                RequiresApproval: false, IsPublicPage: false, IsHidden: false,
                IsPromotedToDirectory: false, CreatedAt: Instant.MinValue,
                Members: [],
                HasBudget: true),
        };
        _teamService.GetTeamsAsync(Arg.Any<CancellationToken>()).Returns(teams);

        var year = await _service.CreateYearAsync("2026", "Budget 2026", Guid.NewGuid());

        year.Year.Should().Be("2026");
        year.Name.Should().Be("Budget 2026");
        year.Status.Should().Be(BudgetYearStatus.Draft);

        await using var ctx = await BudgetDbFactory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        var persistedYear = await ctx.BudgetYears
            .Include(y => y.Groups)
                .ThenInclude(g => g.Categories)
            .Include(y => y.Groups)
                .ThenInclude(g => g.TicketingProjection)
            .FirstAsync(y => y.Id == year.Id, TestContext.Current.CancellationToken);

        persistedYear.Groups.Should().HaveCount(2);

        var deptGroup = persistedYear.Groups.Single(g => g.IsDepartmentGroup);
        deptGroup.Categories.Should().HaveCount(2);
        deptGroup.Categories.Select(c => c.Name).Should().BeEquivalentTo("Kitchen", "Site Ops");

        var ticketingGroup = persistedYear.Groups.Single(g => g.IsTicketingGroup);
        ticketingGroup.TicketingProjection.Should().NotBeNull();
        ticketingGroup.Categories.Select(c => c.Name).Should()
            .BeEquivalentTo("Ticket Revenue", "Processing Fees");

        var auditEntries = await ctx.BudgetAuditLogs
            .Where(a => a.BudgetYearId == year.Id)
            .ToListAsync(TestContext.Current.CancellationToken);
        auditEntries.Should().ContainSingle()
            .Which.Description.Should().Contain("Created budget year");
    }

    // ─── UpdateYearStatusAsync auto-closes previously active years ──────────

    [HumansTheory]
    [InlineData(BudgetYearStatus.Draft)]
    [InlineData(BudgetYearStatus.Closed)]
    public async Task UpdateYearStatusAsync_activating_closes_other_active_years(BudgetYearStatus initialStatus)
    {
        await using (var ctx = await BudgetDbFactory.CreateDbContextAsync(TestContext.Current.CancellationToken))
        {
            ctx.BudgetYears.Add(new BudgetYear
            {
                Id = Guid.NewGuid(),
                Year = "2025",
                Name = "Budget 2025",
                Status = BudgetYearStatus.Active
            });
            ctx.BudgetYears.Add(new BudgetYear
            {
                Id = _yearId,
                Year = "2026",
                Name = "Budget 2026",
                Status = initialStatus
            });
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await _service.UpdateYearStatusAsync(_yearId, BudgetYearStatus.Active, Guid.NewGuid());

        await using var ctx2 = await BudgetDbFactory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        var years = await ctx2.BudgetYears.ToListAsync(TestContext.Current.CancellationToken);

        years.Single(y => string.Equals(y.Year, "2025", StringComparison.Ordinal)).Status
            .Should().Be(BudgetYearStatus.Closed);
        years.Single(y => string.Equals(y.Year, "2026", StringComparison.Ordinal)).Status
            .Should().Be(BudgetYearStatus.Active);

        var auditEntries = await ctx2.BudgetAuditLogs
            .Where(a => a.FieldName == nameof(BudgetYear.Status))
            .ToListAsync(TestContext.Current.CancellationToken);
        auditEntries.Should().HaveCount(2);
    }

    [HumansFact]
    public async Task UpdateYearStatusAsync_archived_year_cannot_replace_the_active_year()
    {
        var activeYearId = Guid.NewGuid();
        await using (var ctx = await BudgetDbFactory.CreateDbContextAsync(TestContext.Current.CancellationToken))
        {
            ctx.BudgetYears.Add(new BudgetYear
            {
                Id = activeYearId,
                Year = "2026",
                Name = "Current budget",
                Status = BudgetYearStatus.Active
            });
            ctx.BudgetYears.Add(new BudgetYear
            {
                Id = _yearId,
                Year = "2025",
                Name = "Old budget",
                Status = BudgetYearStatus.Closed
            });
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        await _service.DeleteYearAsync(_yearId, Guid.NewGuid());
        var existingAudit = await _repository.GetAuditLogAsync(_yearId);

        // A Reactivate form opened while Closed may be submitted after archiving.
        var reactivate = () => _service.UpdateYearStatusAsync(_yearId, BudgetYearStatus.Active, Guid.NewGuid());
        await reactivate.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not found*");

        var active = await _service.GetActiveYearAsync();
        active.Should().NotBeNull();
        active.Id.Should().Be(activeYearId);
        await using var verify = await BudgetDbFactory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        var archived = await verify.BudgetYears.SingleAsync(y => y.Id == _yearId, TestContext.Current.CancellationToken);
        archived.IsDeleted.Should().BeTrue();
        archived.Status.Should().Be(BudgetYearStatus.Closed);
        (await _repository.GetAuditLogAsync(_yearId)).Should().BeEquivalentTo(existingAudit);
        (await verify.BudgetAuditLogs.CountAsync(a => a.BudgetYearId == activeYearId, TestContext.Current.CancellationToken))
            .Should().Be(0);
    }

    [HumansFact]
    public async Task UpdateYearStatusAsync_missing_year_throws()
    {
        var act = () => _service.UpdateYearStatusAsync(Guid.NewGuid(), BudgetYearStatus.Active, Guid.NewGuid());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not found*");
    }

    // ─── UpdateYearAsync writes field audits only for changes ────────────────

    [HumansFact]
    public async Task UpdateYearAsync_writes_field_audit_only_for_changed_fields()
    {
        await using (var ctx = await BudgetDbFactory.CreateDbContextAsync(TestContext.Current.CancellationToken))
        {
            ctx.BudgetYears.Add(new BudgetYear
            {
                Id = _yearId,
                Year = "2026",
                Name = "Budget 2026",
                Status = BudgetYearStatus.Draft
            });
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await _service.UpdateYearAsync(_yearId, "2026", "Budget Twenty Twenty Six", null, Guid.NewGuid());

        await using var ctx2 = await BudgetDbFactory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        var auditEntries = await ctx2.BudgetAuditLogs
            .Where(a => a.BudgetYearId == _yearId)
            .ToListAsync(TestContext.Current.CancellationToken);

        auditEntries.Should().ContainSingle(a => a.FieldName == nameof(BudgetYear.Name));
        auditEntries.Should().NotContain(a => a.FieldName == nameof(BudgetYear.Year));
    }

    [HumansFact]
    public async Task UpdateYearAsync_persists_vendor_event_id_and_audits_the_change()
    {
        await using (var ctx = await BudgetDbFactory.CreateDbContextAsync(TestContext.Current.CancellationToken))
        {
            ctx.BudgetYears.Add(new BudgetYear
            {
                Id = _yearId,
                Year = "2026",
                Name = "Budget 2026",
                Status = BudgetYearStatus.Draft
            });
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await _service.UpdateYearAsync(_yearId, "2026", "Budget 2026", " event-2026 ", Guid.NewGuid());

        var detail = await _service.GetYearByIdAsync(_yearId);
        detail!.VendorEventId.Should().Be("event-2026");
        await using var ctx2 = await BudgetDbFactory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        (await ctx2.BudgetAuditLogs.CountAsync(
            a => a.BudgetYearId == _yearId && a.FieldName == nameof(BudgetYear.VendorEventId),
            TestContext.Current.CancellationToken)).Should().Be(1);
    }

    // ─── DeleteYearAsync refuses active ─────────────────────────────────────

    [HumansFact]
    public async Task DeleteYearAsync_refuses_when_year_is_active()
    {
        await using (var ctx = await BudgetDbFactory.CreateDbContextAsync(TestContext.Current.CancellationToken))
        {
            ctx.BudgetYears.Add(new BudgetYear
            {
                Id = _yearId,
                Year = "2026",
                Name = "Budget 2026",
                Status = BudgetYearStatus.Active
            });
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var act = () => _service.DeleteYearAsync(_yearId, Guid.NewGuid());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*active*");
    }

    [HumansFact]
    public async Task DeleteYearAsync_soft_deletes_when_draft()
    {
        await using (var ctx = await BudgetDbFactory.CreateDbContextAsync(TestContext.Current.CancellationToken))
        {
            ctx.BudgetYears.Add(new BudgetYear
            {
                Id = _yearId,
                Year = "2026",
                Name = "Budget 2026",
                Status = BudgetYearStatus.Draft
            });
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await _service.DeleteYearAsync(_yearId, Guid.NewGuid());

        await using var ctx2 = await BudgetDbFactory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        var year = await ctx2.BudgetYears.SingleAsync(y => y.Id == _yearId, TestContext.Current.CancellationToken);
        year.IsDeleted.Should().BeTrue();
        year.DeletedAt.Should().NotBeNull();
        year.Status.Should().Be(BudgetYearStatus.Closed);
    }

    // ─── Closed year blocks edits ──────────────────────────────────────────

    [HumansFact]
    public async Task CreateGroupAsync_refuses_when_year_is_closed()
    {
        await using (var ctx = await BudgetDbFactory.CreateDbContextAsync(TestContext.Current.CancellationToken))
        {
            ctx.BudgetYears.Add(new BudgetYear
            {
                Id = _yearId,
                Year = "2026",
                Name = "Budget 2026",
                Status = BudgetYearStatus.Closed
            });
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var act = () => _service.CreateGroupAsync(_yearId, "Logistics", false, Guid.NewGuid());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*closed*");
    }

    // ─── SyncTicketingActuals materializes projections inside one save ─────

    [HumansFact]
    public async Task SyncTicketingActualsAsync_upserts_weekly_actuals_and_updates_projection_params()
    {
        var (_, projectionId, revenueCatId, feesCatId) = await SeedTicketingYearAsync();

        var actuals = new List<TicketingWeeklyActuals>
        {
            new(Monday: new LocalDate(2026, 3, 2),
                Sunday: new LocalDate(2026, 3, 8),
                WeekLabel: "Mar 2–Mar 8",
                TicketCount: 10,
                Revenue: 500m,
                StripeFees: 15m,
                TicketTailorFees: 5m,
                StripeFeeTax: 0m,
                StripeFeesTaxUnknown: 15m,
                StripeFeesUntaxed: 0m)
        };

        var changed = await _service.SyncTicketingActualsAsync(_yearId, actuals, actorUserId: null, TestContext.Current.CancellationToken);

        changed.Should().BeGreaterThan(0);

        await using var ctx = await BudgetDbFactory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        var revenueItems = await ctx.BudgetLineItems
            .Where(li => li.BudgetCategoryId == revenueCatId)
            .ToListAsync(TestContext.Current.CancellationToken);
        revenueItems.Should().Contain(li => li.Description.StartsWith("Week of"));

        var feeItems = await ctx.BudgetLineItems
            .Where(li => li.BudgetCategoryId == feesCatId)
            .ToListAsync(TestContext.Current.CancellationToken);
        feeItems.Should().Contain(li => li.Description.StartsWith("Stripe fees:"));
        feeItems.Should().Contain(li => li.Description.StartsWith("TT fees:"));

        var projection = await ctx.TicketingProjections.SingleAsync(p => p.Id == projectionId, TestContext.Current.CancellationToken);
        projection.AverageTicketPrice.Should().Be(50m); // 500 / 10
    }

    [HumansFact]
    public async Task SyncTicketingActualsAsync_splits_stripe_fees_into_one_row_per_fee_vat_class()
    {
        var (_, projectionId, _, feesCatId) = await SeedTicketingYearAsync();
        var ct = TestContext.Current.CancellationToken;
        // One week holding all three classes: 12.10 of unknown tax (pre-capture orders),
        // 6 captured untaxed, 10 captured taxed with 2.10 fee VAT.
        var week = new TicketingWeeklyActuals(
            new LocalDate(2026, 3, 2), new LocalDate(2026, 3, 8), "Mar 2–Mar 8", 10, 1000m,
            StripeFees: 28.10m, TicketTailorFees: 5m, StripeFeeTax: 2.10m,
            StripeFeesTaxUnknown: 12.10m, StripeFeesUntaxed: 6m);

        await _service.SyncTicketingActualsAsync(_yearId, [week], actorUserId: null, ct);

        await using var ctx = await BudgetDbFactory.CreateDbContextAsync(ct);
        var fees = await ctx.BudgetLineItems.Where(li => li.BudgetCategoryId == feesCatId
            && !li.Description.StartsWith("Projected: ")).ToListAsync(ct);
        BudgetLineItem Row(string description) =>
            fees.Single(li => string.Equals(li.Description, description, StringComparison.Ordinal));

        var unknown = Row("Stripe fees: Mar 2–Mar 8");
        unknown.Amount.Should().Be(-12.10m);
        unknown.VatRate.Should().Be(21, because: "fees of unknown tax keep the assumed 21% treatment");
        var untaxed = Row("Stripe fees (no fee VAT): Mar 2–Mar 8");
        untaxed.Amount.Should().Be(-6m);
        untaxed.VatRate.Should().Be(0, because: "Stripe charged no fee VAT, so none is reclaimable");
        var taxed = Row("Stripe fees (fee VAT): Mar 2–Mar 8");
        taxed.Amount.Should().Be(-12.10m, because: "the taxed row records the gross cash cost, fee VAT included");
        taxed.VatRate.Should().Be(21);
        VatCalculation.ComputePortion(taxed.Amount, taxed.VatRate).Should().Be(2.10m);
        Row("TT fees: Mar 2–Mar 8").VatRate
            .Should().Be(21, because: "Stripe's tax bucket says nothing about Ticket Tailor's fee");

        var projection = await ctx.TicketingProjections.SingleAsync(p => p.Id == projectionId, ct);
        projection.StripeFeePercent.Should().Be(3.02m, because: "(28.10 + 2.10) / 1000 — learned gross of fee VAT");
    }

    [HumansFact]
    public async Task SyncTicketingActualsAsync_fee_vat_class_rows_emptied_by_a_later_sync_are_zeroed()
    {
        var (_, _, _, feesCatId) = await SeedTicketingYearAsync();
        var ct = TestContext.Current.CancellationToken;
        var week = new TicketingWeeklyActuals(
            new LocalDate(2026, 3, 2), new LocalDate(2026, 3, 8), "Mar 2–Mar 8", 10, 500m,
            StripeFees: 10m, TicketTailorFees: 0m, StripeFeeTax: 0m,
            StripeFeesTaxUnknown: 10m, StripeFeesUntaxed: 0m);
        await _service.SyncTicketingActualsAsync(_yearId, [week], actorUserId: null, ct);

        // The same orders, now enriched with a captured zero fee tax, move to the untaxed row.
        await _service.SyncTicketingActualsAsync(_yearId,
            [week with { StripeFeesTaxUnknown = 0m, StripeFeesUntaxed = 10m }], actorUserId: null, ct);

        await using var ctx = await BudgetDbFactory.CreateDbContextAsync(ct);
        var fees = await ctx.BudgetLineItems.Where(li => li.BudgetCategoryId == feesCatId
            && !li.Description.StartsWith("Projected: ")).ToListAsync(ct);
        fees.Should().HaveCount(2);
        fees.Single(li => li.Description.StartsWith("Stripe fees:", StringComparison.Ordinal)).Amount.Should().Be(0m);
        fees.Single(li => li.Description.StartsWith("Stripe fees (no fee VAT):", StringComparison.Ordinal)).Amount.Should().Be(-10m);
    }

    [HumansFact]
    public async Task SyncTicketingActualsAsync_clears_existing_fees_when_actuals_become_zero()
    {
        var (_, _, _, feesCatId) = await SeedTicketingYearAsync();
        var ct = TestContext.Current.CancellationToken;
        var week = new TicketingWeeklyActuals(
            new LocalDate(2026, 3, 2), new LocalDate(2026, 3, 8), "Mar 2–Mar 8", 10, 500m, 15m, 5m, 0m, 15m, 0m);
        await _service.SyncTicketingActualsAsync(_yearId, [week], actorUserId: null, ct);

        var corrected = week with { StripeFees = 0m, TicketTailorFees = 0m, StripeFeesTaxUnknown = 0m };
        var nextWeek = corrected with
        {
            Monday = new LocalDate(2026, 3, 9),
            Sunday = new LocalDate(2026, 3, 15),
            WeekLabel = "Mar 9–Mar 15"
        };
        await _service.SyncTicketingActualsAsync(_yearId, [corrected, nextWeek], actorUserId: null, ct);

        await using var ctx = await BudgetDbFactory.CreateDbContextAsync(ct);
        var actualFees = await ctx.BudgetLineItems.Where(li => li.BudgetCategoryId == feesCatId
            && !li.Description.StartsWith("Projected: ")).ToListAsync(ct);
        actualFees.Should().HaveCount(2);
        actualFees.Should().OnlyContain(li => li.Amount == 0m);
        (await ctx.BudgetAuditLogs.CountAsync(a => a.BudgetYearId == _yearId
            && a.Description.StartsWith("Ticketing sync:"), ct)).Should().Be(2);
    }

    [HumansFact]
    public async Task SyncTicketingActualsAsync_zeroes_weeks_absent_from_next_sync()
    {
        var (_, _, revenueCatId, feesCatId) = await SeedTicketingYearAsync();
        var ct = TestContext.Current.CancellationToken;
        var week = new TicketingWeeklyActuals(
            new LocalDate(2026, 3, 2), new LocalDate(2026, 3, 8), "Mar 2–Mar 8", 3, 150m, 15m, 5m, 0m, 15m, 0m);
        await _service.SyncTicketingActualsAsync(_yearId, [week], actorUserId: null, ct);

        // All of the week's orders were refunded: it no longer appears in the input, which now
        // holds only a later week.
        var laterWeek = new TicketingWeeklyActuals(
            new LocalDate(2026, 3, 9), new LocalDate(2026, 3, 15), "Mar 9–Mar 15", 1, 50m, 0m, 0m, 0m, 0m, 0m);
        await _service.SyncTicketingActualsAsync(_yearId, [laterWeek], actorUserId: null, ct);

        await using var ctx = await BudgetDbFactory.CreateDbContextAsync(ct);
        var actuals = await ctx.BudgetLineItems
            .Where(li => (li.BudgetCategoryId == revenueCatId || li.BudgetCategoryId == feesCatId)
                && !li.Description.StartsWith("Projected: ")
                && li.ExpectedDate == new LocalDate(2026, 3, 2))
            .ToListAsync(ct);
        actuals.Should().HaveCount(3, because: "rows are zeroed, not deleted");
        actuals.Should().OnlyContain(li => li.Amount == 0m);
    }

    [HumansFact]
    public async Task SyncTicketingActualsAsync_dropped_zero_amount_week_resets_ticket_count_notes()
    {
        var (_, _, revenueCatId, _) = await SeedTicketingYearAsync();
        var ct = TestContext.Current.CancellationToken;
        // Fully-discounted week: 3 tickets, 0 revenue.
        var week = new TicketingWeeklyActuals(
            new LocalDate(2026, 3, 2), new LocalDate(2026, 3, 8), "Mar 2–Mar 8", 3, 0m, 0m, 0m, 0m, 0m, 0m);
        await _service.SyncTicketingActualsAsync(_yearId, [week], actorUserId: null, ct);

        await _service.SyncTicketingActualsAsync(_yearId, [], actorUserId: null, ct);

        await using var ctx = await BudgetDbFactory.CreateDbContextAsync(ct);
        var revenueRows = await ctx.BudgetLineItems
            .Where(li => li.BudgetCategoryId == revenueCatId
                && !li.Description.StartsWith("Projected: ")
                && li.ExpectedDate == new LocalDate(2026, 3, 2))
            .ToListAsync(ct);
        revenueRows.Should().ContainSingle().Which.Notes.Should().Be("0 tickets");
    }

    [HumansFact]
    public async Task SyncTicketingActualsAsync_all_refunded_empty_input_zeroes_stored_weeks()
    {
        var (_, _, revenueCatId, feesCatId) = await SeedTicketingYearAsync();
        var ct = TestContext.Current.CancellationToken;
        var week = new TicketingWeeklyActuals(
            new LocalDate(2026, 3, 2), new LocalDate(2026, 3, 8), "Mar 2–Mar 8", 3, 150m, 15m, 5m, 0m, 15m, 0m);
        await _service.SyncTicketingActualsAsync(_yearId, [week], actorUserId: null, ct);

        // Every order of the week was refunded: empty input zeroes the stored week.
        await _service.SyncTicketingActualsAsync(_yearId, [], actorUserId: null, ct);

        await using var ctx = await BudgetDbFactory.CreateDbContextAsync(ct);
        var actuals = await ctx.BudgetLineItems
            .Where(li => (li.BudgetCategoryId == revenueCatId || li.BudgetCategoryId == feesCatId)
                && !li.Description.StartsWith("Projected: "))
            .ToListAsync(ct);
        actuals.Should().HaveCount(3);
        actuals.Should().OnlyContain(li => li.Amount == 0m);
    }

    [HumansFact]
    public async Task SyncTicketingActualsAsync_writes_audit_entry_with_null_actor_for_automation()
    {
        await SeedTicketingYearAsync();

        var actuals = new List<TicketingWeeklyActuals>
        {
            new(Monday: new LocalDate(2026, 3, 2),
                Sunday: new LocalDate(2026, 3, 8),
                WeekLabel: "Mar 2–Mar 8",
                TicketCount: 10,
                Revenue: 500m,
                StripeFees: 15m,
                TicketTailorFees: 5m,
                StripeFeeTax: 0m,
                StripeFeesTaxUnknown: 15m,
                StripeFeesUntaxed: 0m)
        };

        await _service.SyncTicketingActualsAsync(_yearId, actuals, actorUserId: null, TestContext.Current.CancellationToken);

        await using var ctx = await BudgetDbFactory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        var auditEntry = await ctx.BudgetAuditLogs
            .SingleAsync(a => a.BudgetYearId == _yearId && a.Description.StartsWith("Ticketing sync:"), TestContext.Current.CancellationToken);
        auditEntry.ActorUserId.Should().BeNull(because: "a null actor marks the entry as automation");
    }

    [HumansFact]
    public async Task RefreshTicketingProjectionsAsync_writes_audit_entry_with_the_acting_user()
    {
        var (groupId, _, _, _) = await SeedTicketingYearAsync();
        await ConfigureProjectionAsync(groupId,
            startDate: new LocalDate(2026, 3, 15),
            eventDate: new LocalDate(2026, 4, 15),
            averageTicketPrice: 100m,
            dailySalesRate: 5m);

        var actor = Guid.NewGuid();
        await _service.RefreshTicketingProjectionsAsync(_yearId, actor, TestContext.Current.CancellationToken);

        await using var ctx = await BudgetDbFactory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        var auditEntry = await ctx.BudgetAuditLogs
            .SingleAsync(a => a.BudgetYearId == _yearId && a.Description.StartsWith("Ticketing projections refreshed:"), TestContext.Current.CancellationToken);
        auditEntry.ActorUserId.Should().Be(actor);
    }

    [HumansFact]
    public async Task Projection_refresh_preserves_ids_and_audit_when_schedule_is_unchanged()
    {
        var (groupId, _, _, _) = await SeedTicketingYearAsync();
        await ConfigureProjectionAsync(groupId, new LocalDate(2026, 3, 15),
            new LocalDate(2026, 4, 15), 100m, 5m);
        var ct = TestContext.Current.CancellationToken;
        await _service.RefreshTicketingProjectionsAsync(_yearId, null, ct);
        List<BudgetLineItem> before;
        int auditsBefore;
        await using (var ctx = await BudgetDbFactory.CreateDbContextAsync(ct))
        {
            before = await ctx.BudgetLineItems.AsNoTracking().ToListAsync(ct);
            auditsBefore = await ctx.BudgetAuditLogs.CountAsync(ct);
        }
        Clock.Advance(Duration.FromHours(1));

        var changed = await _service.RefreshTicketingProjectionsAsync(_yearId, Guid.NewGuid(), ct);

        changed.Should().Be(0);
        await using var stored = await BudgetDbFactory.CreateDbContextAsync(ct);
        (await stored.BudgetLineItems.AsNoTracking().ToListAsync(ct)).Should().BeEquivalentTo(before);
        (await stored.BudgetAuditLogs.CountAsync(ct)).Should().Be(auditsBefore);
    }

    [HumansFact]
    public async Task Projection_refresh_updates_existing_rows_and_removes_only_obsolete_generated_rows()
    {
        var (groupId, _, revenueId, feesId) = await SeedTicketingYearAsync();
        await ConfigureProjectionAsync(groupId, new LocalDate(2026, 3, 15),
            new LocalDate(2026, 4, 15), 100m, 5m);
        var ct = TestContext.Current.CancellationToken;
        var manualId = Guid.NewGuid();
        var actualId = Guid.NewGuid();
        await using (var ctx = await BudgetDbFactory.CreateDbContextAsync(ct))
        {
            var projection = await ctx.TicketingProjections.SingleAsync(ct);
            projection.StripeFeePercent = 2m;
            await ctx.BudgetLineItems.AddRangeAsync([
                new BudgetLineItem { Id = manualId, BudgetCategoryId = revenueId,
                    Description = "Projected: manual entry", Amount = 12m },
                new BudgetLineItem { Id = actualId, BudgetCategoryId = feesId,
                    Description = "Stripe fees: completed week", Amount = -2m, IsAutoGenerated = true }], ct);
            await ctx.SaveChangesAsync(ct);
        }
        await _service.RefreshTicketingProjectionsAsync(_yearId, null, ct);
        Dictionary<LocalDate, Guid> previousRevenue;
        await using (var ctx = await BudgetDbFactory.CreateDbContextAsync(ct))
        {
            previousRevenue = (await ctx.BudgetLineItems.Where(li => li.BudgetCategoryId == revenueId
                && li.IsAutoGenerated).ToListAsync(ct)).ToDictionary(li => li.ExpectedDate!.Value, li => li.Id);
            var projection = await ctx.TicketingProjections.SingleAsync(ct);
            projection.EventDate = new LocalDate(2026, 4, 8);
            projection.AverageTicketPrice = 50m;
            projection.StripeFeePercent = 0m;
            await ctx.SaveChangesAsync(ct);
        }

        await _service.RefreshTicketingProjectionsAsync(_yearId, Guid.NewGuid(), ct);

        await using var stored = await BudgetDbFactory.CreateDbContextAsync(ct);
        var lines = await stored.BudgetLineItems.ToListAsync(ct);
        var revenue = lines.Where(li => li.BudgetCategoryId == revenueId && li.IsAutoGenerated).ToList();
        revenue.Should().HaveCount(2);
        revenue.Single(li => li.ExpectedDate == new LocalDate(2026, 3, 30)).Id
            .Should().Be(previousRevenue[new LocalDate(2026, 3, 30)]);
        revenue.Single(li => li.ExpectedDate == new LocalDate(2026, 3, 30)).Amount.Should().Be(1750m);
        lines.Should().Contain(li => li.Id == manualId && li.Amount == 12m);
        lines.Should().Contain(li => li.Id == actualId && li.Amount == -2m);
        lines.Should().NotContain(li => li.BudgetCategoryId == feesId && li.Description.StartsWith("Projected:"));
        lines.Should().NotContain(li => li.Id == previousRevenue[new LocalDate(2026, 4, 13)]);

        Clock.Reset(Instant.FromUtc(2026, 4, 9, 12, 0));
        await _service.RefreshTicketingProjectionsAsync(_yearId, null, ct);
        await using var ended = await BudgetDbFactory.CreateDbContextAsync(ct);
        (await ended.BudgetLineItems.Select(li => li.Id).ToListAsync(ct)).Should().BeEquivalentTo([manualId, actualId]);
    }

    [HumansFact]
    public async Task SyncTicketingActualsAsync_is_noop_when_no_ticketing_group()
    {
        await using (var ctx = await BudgetDbFactory.CreateDbContextAsync(TestContext.Current.CancellationToken))
        {
            ctx.BudgetYears.Add(new BudgetYear
            {
                Id = _yearId,
                Year = "2026",
                Name = "Budget 2026",
                Status = BudgetYearStatus.Draft
            });
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var result = await _service.SyncTicketingActualsAsync(
            _yearId,
            new List<TicketingWeeklyActuals>(), actorUserId: null, TestContext.Current.CancellationToken);

        result.Should().Be(0);
    }

    [HumansTheory]
    [InlineData("en")]
    [InlineData("es")]
    [InlineData("de")]
    [InlineData("it")]
    [InlineData("fr")]
    [InlineData("ca")]
    public async Task RefreshTicketingProjectionsAsync_materializes_projected_weeks_when_projection_is_valid(string culture)
    {
        using var scope = new Humans.Base.Extensions.CultureScope(culture);
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en");
        var (groupId, _, revenueCatId, _) = await SeedTicketingYearAsync();
        await ConfigureProjectionAsync(groupId,
            startDate: new LocalDate(2026, 3, 15),
            eventDate: new LocalDate(2026, 4, 15),
            averageTicketPrice: 100m,
            dailySalesRate: 5m);

        var created = await _service.RefreshTicketingProjectionsAsync(_yearId, actorUserId: null, TestContext.Current.CancellationToken);

        created.Should().BeGreaterThan(0);

        await using var ctx = await BudgetDbFactory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        var projectedRevenueItems = await ctx.BudgetLineItems
            .Where(li => li.BudgetCategoryId == revenueCatId
                && li.Description.StartsWith("Projected:"))
            .ToListAsync(TestContext.Current.CancellationToken);
        projectedRevenueItems.Should().NotBeEmpty();
        projectedRevenueItems.Should().AllSatisfy(item => item.Description.Should().MatchRegex(
            "^Projected: Week of (Mon|Tue|Wed|Thu|Fri|Sat|Sun) (Mar|Apr) [0-9]+–(Mon|Tue|Wed|Thu|Fri|Sat|Sun) (Mar|Apr) [0-9]+$"));
        CultureInfo.CurrentCulture.Name.Should().Be("en");
        CultureInfo.CurrentUICulture.Name.Should().Be(culture);
    }

    [HumansTheory]
    [InlineData(22, 5, 47, 15, 5, 15)]
    [InlineData(1, 5, 35, 15, 5, 15)]
    [InlineData(22, 0, 12, 15, 5, 1)]
    [InlineData(22, 5, 47, 13, 5, 5)]
    [InlineData(22, 0, 12, 13, 5, 1)]
    [InlineData(22, 5, 47, 12, 4, 35)]
    public async Task Ticketing_projection_preview_matches_materialized_weeks(
        int startDay, int dailyRate, int firstWeekTickets, int eventDay, int weeks, int lastWeekTickets)
    {
        Clock.Reset(Instant.FromUtc(2026, 3, 17, 12, 0));
        var (groupId, _, revenueCatId, feesCatId) = await SeedTicketingYearAsync();
        await ConfigureProjectionAsync(groupId, new LocalDate(2026, 3, startDay),
            new LocalDate(2026, 4, eventDay), 19.99m, dailyRate);
        await using (var ctx = await BudgetDbFactory.CreateDbContextAsync(TestContext.Current.CancellationToken))
        {
            var projection = await ctx.TicketingProjections.SingleAsync(
                p => p.BudgetGroupId == groupId, TestContext.Current.CancellationToken);
            projection.InitialSalesCount = 12;
            projection.StripeFeePercent = 1.5m;
            projection.StripeFeeFixed = 0.25m;
            projection.TicketTailorFeePercent = 3m;
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var preview = await _service.GetTicketingProjectionEntriesAsync(
            groupId, TestContext.Current.CancellationToken);
        preview.Should().HaveCount(weeks);
        preview[0].WeekStart.Should().Be(new LocalDate(2026, 3, 16));
        preview[0].ProjectedTickets.Should().Be(firstWeekTickets);
        preview[^1].WeekEnd.Should().Be(new LocalDate(2026, 4, eventDay));
        preview[^1].ProjectedTickets.Should().Be(lastWeekTickets);

        await _service.RefreshTicketingProjectionsAsync(_yearId, null, TestContext.Current.CancellationToken);

        await using var stored = await BudgetDbFactory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        var lines = await stored.BudgetLineItems.Where(li => li.IsAutoGenerated)
            .ToListAsync(TestContext.Current.CancellationToken);
        foreach (var week in preview)
        {
            var revenue = lines.Single(li => li.BudgetCategoryId == revenueCatId && li.ExpectedDate == week.WeekStart);
            revenue.Amount.Should().Be(week.ProjectedRevenue);
            revenue.Notes.Should().Be($"~{week.ProjectedTickets} tickets");
            lines.Single(li => li.BudgetCategoryId == feesCatId && li.ExpectedDate == week.WeekStart
                && li.Description.Contains("Stripe fees:", StringComparison.Ordinal)).Amount.Should().Be(-week.ProjectedStripeFees);
            lines.Single(li => li.BudgetCategoryId == feesCatId && li.ExpectedDate == week.WeekStart
                && li.Description.Contains("TT fees:", StringComparison.Ordinal)).Amount.Should().Be(-week.ProjectedTtFees);
        }
    }

    [HumansFact]
    public async Task Ticketing_projection_stops_once_a_Monday_event_has_passed()
    {
        Clock.Reset(Instant.FromUtc(2026, 4, 15, 12, 0));
        var (groupId, _, _, _) = await SeedTicketingYearAsync();
        await ConfigureProjectionAsync(groupId, new LocalDate(2026, 3, 22),
            new LocalDate(2026, 4, 13), 19.99m, 5);

        var preview = await _service.GetTicketingProjectionEntriesAsync(
            groupId, TestContext.Current.CancellationToken);

        preview.Should().BeEmpty();
    }

    // Guards the ordering invariant: materialization runs in the repo AFTER
    // UpdateProjectionFromActuals, so projected items use the newly-learned
    // average price / fee percentages rather than the pre-sync ones.
    [HumansFact]
    public async Task SyncTicketingActualsAsync_projected_items_use_post_update_avg_price_not_pre_sync_value()
    {
        var (groupId, _, revenueCatId, _) = await SeedTicketingYearAsync();

        // Projection configured with AverageTicketPrice=100. After the sync,
        // actuals (20 tickets, 1000 revenue) will re-learn AverageTicketPrice=50.
        // The Projected: revenue line items must use 50, not 100.
        await ConfigureProjectionAsync(groupId,
            startDate: new LocalDate(2026, 4, 6), // Monday after the FakeClock today (2026-03-31).
            eventDate: new LocalDate(2026, 5, 4),
            averageTicketPrice: 100m,
            dailySalesRate: 5m);

        var actuals = new List<TicketingWeeklyActuals>
        {
            new(Monday: new LocalDate(2026, 3, 16),
                Sunday: new LocalDate(2026, 3, 22),
                WeekLabel: "Mar 16–Mar 22",
                TicketCount: 20,
                Revenue: 1000m,
                StripeFees: 0m,
                TicketTailorFees: 0m,
                StripeFeeTax: 0m,
                StripeFeesTaxUnknown: 0m,
                StripeFeesUntaxed: 0m)
        };

        await _service.SyncTicketingActualsAsync(_yearId, actuals, actorUserId: null, TestContext.Current.CancellationToken);

        await using var ctx = await BudgetDbFactory.CreateDbContextAsync(TestContext.Current.CancellationToken);

        // The projection was updated in-place before materialization.
        var projection = await ctx.TicketingProjections.SingleAsync(p => p.BudgetGroupId == groupId, TestContext.Current.CancellationToken);
        projection.AverageTicketPrice.Should().Be(50m);

        // Projected: revenue items must reflect the new AvgPrice of 50.
        // Given 5 tickets/day * 7 days = 35 tickets/week at 50 = 1750/week.
        // Assert divisibility by 50 rather than exact totals: independent of
        // initial-burst math.
        var projectedItems = await ctx.BudgetLineItems
            .Where(li => li.BudgetCategoryId == revenueCatId
                && li.Description.StartsWith("Projected:"))
            .ToListAsync(TestContext.Current.CancellationToken);

        projectedItems.Should().NotBeEmpty("projection is valid and covers multiple weeks before event");

        foreach (var item in projectedItems)
        {
            // Revenue = tickets * 50 (new price). If stale 100 was used, it'd be tickets * 100.
            // Parse ticket count from notes "~N tickets" and verify amount == count * 50.
            item.Notes.Should().NotBeNullOrEmpty();
            var notesClean = item.Notes!.TrimStart('~');
            var spaceIdx = notesClean.IndexOf(' ', StringComparison.Ordinal);
            spaceIdx.Should().BeGreaterThan(0);
            var ticketCount = int.Parse(
                notesClean[..spaceIdx],
                CultureInfo.InvariantCulture);

            item.Amount.Should().Be(
                ticketCount * 50m,
                because: $"projected revenue must use post-sync learned price (50), not pre-sync value (100); item '{item.Description}' had {ticketCount} tickets");
        }
    }

    // ─── Sync never touches hand-entered items ──────────────────────────────

    [HumansFact]
    public async Task SyncTicketingActualsAsync_never_touches_hand_entered_line_items()
    {
        var (groupId, _, revenueCatId, _) = await SeedTicketingYearAsync();
        await ConfigureProjectionAsync(groupId,
            startDate: new LocalDate(2026, 4, 6),
            eventDate: new LocalDate(2026, 5, 4),
            averageTicketPrice: 50m,
            dailySalesRate: 5m);

        var manualId = Guid.NewGuid();
        var manualProjectedId = Guid.NewGuid();
        await using (var ctx = await BudgetDbFactory.CreateDbContextAsync(TestContext.Current.CancellationToken))
        {
            ctx.BudgetLineItems.Add(new BudgetLineItem
            {
                Id = manualId,
                BudgetCategoryId = revenueCatId,
                Description = "Vendor deposit",
                Amount = 123.45m,
                Notes = "hand-entered",
                IsAutoGenerated = false
            });
            // Hand-entered item that happens to carry the sweep prefix: only
            // IsAutoGenerated items may be removed or upserted by the sync.
            ctx.BudgetLineItems.Add(new BudgetLineItem
            {
                Id = manualProjectedId,
                BudgetCategoryId = revenueCatId,
                Description = "Projected: manual note",
                Amount = -10m,
                IsAutoGenerated = false
            });
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var actuals = new List<TicketingWeeklyActuals>
        {
            new(Monday: new LocalDate(2026, 3, 16),
                Sunday: new LocalDate(2026, 3, 22),
                WeekLabel: "Mar 16-Mar 22",
                TicketCount: 20,
                Revenue: 1000m,
                StripeFees: 15m,
                TicketTailorFees: 30m,
                StripeFeeTax: 0m,
                StripeFeesTaxUnknown: 15m,
                StripeFeesUntaxed: 0m)
        };

        await _service.SyncTicketingActualsAsync(_yearId, actuals, actorUserId: null, TestContext.Current.CancellationToken);

        await using var verify = await BudgetDbFactory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        var manual = await verify.BudgetLineItems.SingleAsync(li => li.Id == manualId, TestContext.Current.CancellationToken);
        manual.Description.Should().Be("Vendor deposit");
        manual.Amount.Should().Be(123.45m);
        manual.Notes.Should().Be("hand-entered");
        manual.IsAutoGenerated.Should().BeFalse();

        var manualProjected = await verify.BudgetLineItems.SingleAsync(li => li.Id == manualProjectedId, TestContext.Current.CancellationToken);
        manualProjected.Amount.Should().Be(-10m);
        manualProjected.IsAutoGenerated.Should().BeFalse();

        (await verify.BudgetLineItems.AnyAsync(li => li.IsAutoGenerated, TestContext.Current.CancellationToken))
            .Should().BeTrue(because: "the sync must still materialize its own auto items alongside");
    }

    // ─── Summary computation ────────────────────────────────────────────────

    [HumansFact]
    public void ComputeBudgetSummary_excludes_cashflow_only_and_breaks_out_vat()
    {
        var groupId = Guid.NewGuid();
        var catSales = new BudgetCategoryDetail(Guid.NewGuid(), groupId, "Sales", 0m, ExpenditureType.OpEx, null, 0,
        [
            new BudgetLineItemDetail(Guid.NewGuid(), Guid.NewGuid(), "Ticket income", 110m, null, null, new LocalDate(2026, 5, 1), 10, false, false, 0)
        ]);
        var catOps = new BudgetCategoryDetail(Guid.NewGuid(), groupId, "Ops", 0m, ExpenditureType.OpEx, null, 0,
        [
            new BudgetLineItemDetail(Guid.NewGuid(), Guid.NewGuid(), "Equipment", -121m, null, null, new LocalDate(2026, 5, 1), 21, false, false, 0)
        ]);
        var catDonations = new BudgetCategoryDetail(Guid.NewGuid(), groupId, "Donations", 0m, ExpenditureType.OpEx, null, 0,
        [
            new BudgetLineItemDetail(Guid.NewGuid(), Guid.NewGuid(), "Donations", 999m, null, null, null, 0, false, true, 0)
        ]);
        IReadOnlyList<BudgetGroupDetail> groups =
        [
            new(groupId, Guid.NewGuid(), "Main", 0, false, false, false, null, [catSales, catOps, catDonations])
        ];

        var summary = _service.ComputeBudgetSummary(groups);

        // VAT-inclusive amounts: the 110 income at 10% carries 10 of VAT (a liability);
        // the 121 expense at 21% carries 21 (a credit). The 999 cashflow-only item is out.
        summary.TotalIncome.Should().Be(131m);
        summary.TotalExpenses.Should().Be(-131m);
        summary.NetBalance.Should().Be(0m);
        summary.IncomeSlices.Select(sl => sl.Name).Should().BeEquivalentTo(["Sales", "VAT Credits"]);
        summary.ExpenseSlices.Select(sl => sl.Name).Should().BeEquivalentTo(["Ops", "VAT Liability"]);
    }

    [HumansFact]
    public void ComputeBudgetSummaryWithBuffers_totals_line_items_excluding_cashflow_only()
    {
        var groupId = Guid.NewGuid();
        var cat = new BudgetCategoryDetail(Guid.NewGuid(), groupId, "Ops", 0m, ExpenditureType.OpEx, null, 0,
        [
            new BudgetLineItemDetail(Guid.NewGuid(), Guid.NewGuid(), "Income", 200m, null, null, null, 0, false, false, 0),
            new BudgetLineItemDetail(Guid.NewGuid(), Guid.NewGuid(), "Equipment", -50m, null, null, null, 0, false, false, 0),
            new BudgetLineItemDetail(Guid.NewGuid(), Guid.NewGuid(), "Loan", 999m, null, null, null, 0, false, true, 0)
        ]);
        IReadOnlyList<BudgetGroupDetail> groups =
        [
            new(groupId, Guid.NewGuid(), "Main", 0, false, false, false, null, [cat])
        ];

        _service.ComputeBudgetSummaryWithBuffers(groups).TotalLineItems.Should().Be(150m);
    }

    // ─── GDPR export follows the merge chain ────────────────────────────────

    [HumansFact]
    public async Task ContributeForUserAsync_includes_audit_rows_of_merged_accounts_only()
    {
        var userId = Guid.NewGuid();
        var sourceId = Guid.NewGuid();
        await using (var ctx = await BudgetDbFactory.CreateDbContextAsync(TestContext.Current.CancellationToken))
        {
            ctx.BudgetYears.Add(new BudgetYear { Id = _yearId, Year = "2026", Name = "Budget 2026" });
            foreach (var (actor, description) in new (Guid?, string)[]
                     {
                         (userId, "own"), (sourceId, "merged"), (Guid.NewGuid(), "stranger"), (null, "automation")
                     })
            {
                ctx.BudgetAuditLogs.Add(new BudgetAuditLog
                {
                    Id = Guid.NewGuid(),
                    BudgetYearId = _yearId,
                    EntityType = "BudgetGroup",
                    Description = description,
                    ActorUserId = actor,
                    OccurredAt = Clock.GetCurrentInstant()
                });
            }
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        var info = UserInfo.Create(new User { Id = userId }, [], [], [], null, []) with { MergedUserIds = [sourceId] };
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(info);

        var slices = await _service.ContributeForUserAsync(userId, TestContext.Current.CancellationToken);

        var json = System.Text.Json.JsonSerializer.Serialize(slices.Single().Data);
        json.Should().Contain("\"own\"").And.Contain("\"merged\"");
        json.Should().NotContain("stranger").And.NotContain("automation");
    }

    // ─── Closed year gates every tree mutation ──────────────────────────────

    public static TheoryData<string> ClosedYearMutations => new()
    {
        "create-group", "update-group", "delete-group",
        "create-category", "update-category", "delete-category",
        "create-line-item", "update-line-item", "delete-line-item",
        "sync-departments", "ensure-ticketing-group", "update-ticketing-projection",
        "update-year", "sync-ticketing-actuals", "refresh-ticketing-projections"
    };

    [HumansTheory]
    [MemberData(nameof(ClosedYearMutations))]
    public async Task Repository_mutations_refuse_when_year_is_closed(string mutation)
    {
        // One closed year carrying a normal group + category + line item and a
        // ticketing group with a projection row, so every mutation has a target.
        var groupId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var lineItemId = Guid.NewGuid();
        var ticketingGroupId = Guid.NewGuid();
        await using (var ctx = await BudgetDbFactory.CreateDbContextAsync(TestContext.Current.CancellationToken))
        {
            ctx.BudgetYears.Add(new BudgetYear { Id = _yearId, Year = "2026", Name = "Budget 2026", Status = BudgetYearStatus.Closed });
            ctx.BudgetGroups.Add(new BudgetGroup { Id = groupId, BudgetYearId = _yearId, Name = "Departments" });
            ctx.BudgetCategories.Add(new BudgetCategory { Id = categoryId, BudgetGroupId = groupId, Name = "Operations" });
            ctx.BudgetLineItems.Add(new BudgetLineItem { Id = lineItemId, BudgetCategoryId = categoryId, Description = "Rent", Amount = -1m });
            ctx.BudgetGroups.Add(new BudgetGroup { Id = ticketingGroupId, BudgetYearId = _yearId, Name = "Ticketing", IsTicketingGroup = true });
            ctx.TicketingProjections.Add(new TicketingProjection { Id = Guid.NewGuid(), BudgetGroupId = ticketingGroupId });
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var actor = Guid.NewGuid();
        var now = Clock.GetCurrentInstant();
        var ct = TestContext.Current.CancellationToken;
        Func<Task> act = mutation switch
        {
            "create-group" => () => _repository.CreateGroupAsync(_yearId, "New", false, actor, now, ct),
            "update-group" => () => _repository.UpdateGroupAsync(groupId, "Renamed", 1, false, actor, now, ct),
            "delete-group" => () => _repository.DeleteGroupAsync(groupId, actor, now, ct),
            "create-category" => () => _repository.CreateCategoryAsync(groupId, "New", 0m, ExpenditureType.OpEx, null, actor, now, ct),
            "update-category" => () => _repository.UpdateCategoryAsync(categoryId, "Renamed", 1m, ExpenditureType.OpEx, actor, now, ct),
            "delete-category" => () => _repository.DeleteCategoryAsync(categoryId, actor, now, ct),
            "create-line-item" => () => _repository.CreateLineItemAsync(new BudgetLineItemDraft(categoryId, "New", 1m, null, null, null, 0), actor, now, ct),
            "update-line-item" => () => _repository.UpdateLineItemAsync(new BudgetLineItemUpdate(lineItemId, "Renamed", 2m, null, null, null, 0), actor, now, ct),
            "delete-line-item" => () => _repository.DeleteLineItemAsync(lineItemId, actor, now, ct),
            "sync-departments" => () => _repository.SyncDepartmentCategoriesAsync(_yearId, [new BudgetableTeamRef(Guid.NewGuid(), "Team")], actor, now, ct),
            "ensure-ticketing-group" => () => _repository.EnsureTicketingGroupAsync(_yearId, actor, now, ct),
            "update-ticketing-projection" => () => _repository.UpdateTicketingProjectionAsync(new TicketingProjectionUpdate(ticketingGroupId, null, null, 0, 0m, 0m, 10, 0m, 0m, 0m), actor, now, ct),
            "update-year" => () => _repository.UpdateYearAsync(_yearId, "2027", "Renamed", null, actor, now, ct),
            "sync-ticketing-actuals" => () => _repository.SyncTicketingActualsAsync(_yearId, [], Clock.GetCurrentInstant().InUtc().Date, actor, now, ct),
            "refresh-ticketing-projections" => () => _repository.RefreshTicketingProjectionsAsync(_yearId, Clock.GetCurrentInstant().InUtc().Date, actor, now, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null)
        };

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*closed*");
    }

    // ─── Seeding helpers ────────────────────────────────────────────────────

    private async Task<BudgetCategory> SeedCategoryAsync(bool isRestricted = false, bool isTicketingGroup = false)
    {
        var year = new BudgetYear
        {
            Id = Guid.NewGuid(),
            Year = "2026",
            Name = "Budget 2026"
        };
        var group = new BudgetGroup
        {
            Id = Guid.NewGuid(),
            BudgetYearId = year.Id,
            BudgetYear = year,
            Name = "Departments",
            IsRestricted = isRestricted,
            IsTicketingGroup = isTicketingGroup
        };
        var category = new BudgetCategory
        {
            Id = Guid.NewGuid(),
            BudgetGroupId = group.Id,
            BudgetGroup = group,
            Name = "Operations"
        };

        await using var ctx = await BudgetDbFactory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        ctx.BudgetYears.Add(year);
        ctx.BudgetGroups.Add(group);
        ctx.BudgetCategories.Add(category);
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        return category;
    }

    private async Task<(Guid GroupId, Guid ProjectionId, Guid RevenueCatId, Guid FeesCatId)>
        SeedTicketingYearAsync()
    {
        var groupId = Guid.NewGuid();
        var projectionId = Guid.NewGuid();
        var revenueCatId = Guid.NewGuid();
        var feesCatId = Guid.NewGuid();

        await using var ctx = await BudgetDbFactory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        ctx.BudgetYears.Add(new BudgetYear
        {
            Id = _yearId,
            Year = "2026",
            Name = "Budget 2026",
            Status = BudgetYearStatus.Active
        });

        ctx.BudgetGroups.Add(new BudgetGroup
        {
            Id = groupId,
            BudgetYearId = _yearId,
            Name = "Ticketing",
            IsTicketingGroup = true
        });

        ctx.BudgetCategories.Add(new BudgetCategory
        {
            Id = revenueCatId,
            BudgetGroupId = groupId,
            Name = "Ticket Revenue"
        });

        ctx.BudgetCategories.Add(new BudgetCategory
        {
            Id = feesCatId,
            BudgetGroupId = groupId,
            Name = "Processing Fees"
        });

        ctx.TicketingProjections.Add(new TicketingProjection
        {
            Id = projectionId,
            BudgetGroupId = groupId
        });

        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        return (groupId, projectionId, revenueCatId, feesCatId);
    }

    private async Task ConfigureProjectionAsync(
        Guid groupId,
        LocalDate startDate,
        LocalDate eventDate,
        decimal averageTicketPrice,
        decimal dailySalesRate)
    {
        await using var ctx = await BudgetDbFactory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        var projection = await ctx.TicketingProjections.SingleAsync(p => p.BudgetGroupId == groupId, TestContext.Current.CancellationToken);
        projection.StartDate = startDate;
        projection.EventDate = eventDate;
        projection.AverageTicketPrice = averageTicketPrice;
        projection.DailySalesRate = dailySalesRate;
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
    }
}
