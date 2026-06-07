using System.Security.Claims;
using AwesomeAssertions;
using Humans.Application.Tests.Infrastructure;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Web.Authorization;
using Humans.Web.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Humans.Application.Tests.Controllers;

public class UsersAdminAccountMergesControllerTests
{
    private readonly IUserServiceRead _userService = Substitute.For<IUserServiceRead>();
    private readonly IAccountMergeService _mergeService = Substitute.For<IAccountMergeService>();
    private readonly IDuplicateAccountService _duplicateService = Substitute.For<IDuplicateAccountService>();
    private readonly Guid _adminUserId = Guid.NewGuid();

    public UsersAdminAccountMergesControllerTests()
    {
        var adminUser = new User { Id = _adminUserId, PreferredLanguage = "en" };
        _userService.GetUserInfoAsync(_adminUserId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(adminUser.ToUserInfo()));
    }

    private UsersAdminAccountMergesController BuildController()
    {
        var c = new UsersAdminAccountMergesController(
            _userService,
            _mergeService,
            _duplicateService,
            NullLogger<UsersAdminAccountMergesController>.Instance);

        var identity = new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, _adminUserId.ToString())
        ], authenticationType: "TestAuth");
        var principal = new ClaimsPrincipal(identity);

        var services = new ServiceCollection();
        services.AddLogging();
        var httpContext = new DefaultHttpContext
        {
            User = principal,
            RequestServices = services.BuildServiceProvider(),
        };

        c.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext,
            ActionDescriptor = new ControllerActionDescriptor { ActionName = "Test" },
            RouteData = new RouteData(),
        };
        c.TempData = new TempDataDictionary(httpContext, Substitute.For<ITempDataProvider>());
        c.Url = Substitute.For<IUrlHelper>();
        return c;
    }

    [HumansFact]
    public void Controller_HasAdminOnlyPolicyAttribute()
    {
        var attr = typeof(UsersAdminAccountMergesController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .Cast<AuthorizeAttribute>()
            .Single();
        attr.Policy.Should().Be(PolicyNames.AdminOnly);
    }

    [HumansFact]
    public async Task Merge_CallsMergeAsyncWithSurvivorArchivedAndAdmin_RedirectsToIndex()
    {
        var survivor = Guid.NewGuid();
        var archived = Guid.NewGuid();

        var result = await BuildController().Merge(survivor, archived, "note", CancellationToken.None);

        await _mergeService.Received(1).MergeAsync(
            survivor, archived, _adminUserId, "note", null, Arg.Any<CancellationToken>());
        result.Should().BeOfType<RedirectToActionResult>()
            .Which.ActionName.Should().Be(nameof(UsersAdminAccountMergesController.Index));
    }

    [HumansFact]
    public async Task Merge_WhenMergeThrowsInvalidOperation_SetsErrorAndRedirectsToIndex()
    {
        var survivor = Guid.NewGuid();
        var archived = Guid.NewGuid();
        _mergeService.MergeAsync(survivor, archived, _adminUserId, Arg.Any<string?>(), null, Arg.Any<CancellationToken>())
            .Returns(_ => throw new InvalidOperationException("boom"));

        var result = await BuildController().Merge(survivor, archived, notes: null, CancellationToken.None);

        result.Should().BeOfType<RedirectToActionResult>()
            .Which.ActionName.Should().Be(nameof(UsersAdminAccountMergesController.Index));
    }

    [HumansFact]
    public async Task MergeRequest_CallsAcceptAsync_RedirectsToIndex()
    {
        var requestId = Guid.NewGuid();
        var survivor = Guid.NewGuid();

        var result = await BuildController().MergeRequest(requestId, survivor, "note", CancellationToken.None);

        await _mergeService.Received(1).AcceptAsync(
            requestId, _adminUserId, survivor, "note", Arg.Any<CancellationToken>());
        result.Should().BeOfType<RedirectToActionResult>()
            .Which.ActionName.Should().Be(nameof(UsersAdminAccountMergesController.Index));
    }

    [HumansFact]
    public async Task Dismiss_CallsRejectAsync_RedirectsToIndex()
    {
        var requestId = Guid.NewGuid();

        var result = await BuildController().Dismiss(requestId, "no thanks", CancellationToken.None);

        await _mergeService.Received(1).RejectAsync(
            requestId, _adminUserId, "no thanks", Arg.Any<CancellationToken>());
        result.Should().BeOfType<RedirectToActionResult>()
            .Which.ActionName.Should().Be(nameof(UsersAdminAccountMergesController.Index));
    }

    [HumansFact]
    public async Task Close_CallsReconcileMergedRequestAsync_RedirectsToIndex()
    {
        var requestId = Guid.NewGuid();

        var result = await BuildController().Close(requestId, CancellationToken.None);

        await _mergeService.Received(1).ReconcileMergedRequestAsync(
            requestId, _adminUserId, Arg.Any<CancellationToken>());
        result.Should().BeOfType<RedirectToActionResult>()
            .Which.ActionName.Should().Be(nameof(UsersAdminAccountMergesController.Index));
    }

    [HumansFact]
    public async Task Close_WhenReconcileThrowsInvalidOperation_SetsErrorAndRedirectsToIndex()
    {
        var requestId = Guid.NewGuid();
        _mergeService.ReconcileMergedRequestAsync(requestId, _adminUserId, Arg.Any<CancellationToken>())
            .Returns(_ => throw new InvalidOperationException("not merged into each other"));

        var result = await BuildController().Close(requestId, CancellationToken.None);

        result.Should().BeOfType<RedirectToActionResult>()
            .Which.ActionName.Should().Be(nameof(UsersAdminAccountMergesController.Index));
    }
}
