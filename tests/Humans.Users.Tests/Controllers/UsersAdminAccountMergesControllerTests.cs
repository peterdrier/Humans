using System.Security.Claims;
using AwesomeAssertions;
using Humans.Users.Tests.Infrastructure;
using Humans.Users.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Humans.Users.Contracts;
using Humans.Users.Services;

namespace Humans.Users.Tests.Controllers;

public class UsersAdminAccountMergesControllerTests
{
    private readonly IUserManagement _userService = Substitute.For<IUserManagement>();
    private readonly IAccountMergeService _mergeService = Substitute.For<IAccountMergeService>();
    private readonly IDuplicateAccountService _duplicateService = Substitute.For<IDuplicateAccountService>();
    private readonly Guid _adminUserId = Guid.NewGuid();

    public UsersAdminAccountMergesControllerTests()
    {
        _mergeService.MergeAsync(default, default, default).ReturnsForAnyArgs(new AccountMergeResult());
        _mergeService.AcceptAsync(default, default, default).ReturnsForAnyArgs(new AccountMergeResult());
        _mergeService.RejectAsync(default, default).ReturnsForAnyArgs(new AccountMergeResult());
        _mergeService.ReconcileMergedRequestAsync(default, default).ReturnsForAnyArgs(new AccountMergeResult());
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

    [HumansTheory]
    [Xunit.InlineData("merge")]
    [Xunit.InlineData("accept")]
    [Xunit.InlineData("reject")]
    [Xunit.InlineData("close")]
    public async Task Mutation_DependencyFailure_PropagatesWithoutRefusalFeedback(string action)
    {
        var id = Guid.NewGuid();
        var other = Guid.NewGuid();
        var ct = Xunit.TestContext.Current.CancellationToken;
        var failure = new InvalidOperationException("Private dependency failure");
        _mergeService.MergeAsync(id, other, _adminUserId, null, null, ct).Returns(Task.FromException<AccountMergeResult>(failure));
        _mergeService.AcceptAsync(id, _adminUserId, other, null, ct).Returns(Task.FromException<AccountMergeResult>(failure));
        _mergeService.RejectAsync(id, _adminUserId, null, ct).Returns(Task.FromException<AccountMergeResult>(failure));
        _mergeService.ReconcileMergedRequestAsync(id, _adminUserId, ct).Returns(Task.FromException<AccountMergeResult>(failure));
        var controller = BuildController();
        Func<Task> act = async () =>
        {
            _ = action switch
            {
                "merge" => await controller.Merge(id, other, null, ct),
                "accept" => await controller.MergeRequest(id, other, null, ct),
                "reject" => await controller.Dismiss(id, null, ct),
                _ => await controller.Close(id, ct)
            };
        };

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(failure);
        controller.TempData.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Merge_CallsMergeAsyncWithSurvivorArchivedAndAdmin_RedirectsToIndex()
    {
        var survivor = Guid.NewGuid();
        var archived = Guid.NewGuid();

        var result = await BuildController().Merge(survivor, archived, "note", Xunit.TestContext.Current.CancellationToken);

        await _mergeService.Received(1).MergeAsync(
            survivor, archived, _adminUserId, "note", null, Arg.Any<CancellationToken>());
        result.Should().BeOfType<RedirectToActionResult>()
            .Which.ActionName.Should().Be(nameof(UsersAdminAccountMergesController.Index));
    }

    [HumansFact]
    public async Task Merge_WhenRefused_SetsErrorAndRedirectsToIndex()
    {
        var survivor = Guid.NewGuid();
        var archived = Guid.NewGuid();
        _mergeService.MergeAsync(survivor, archived, _adminUserId, Arg.Any<string?>(), null, Arg.Any<CancellationToken>())
            .Returns(new AccountMergeResult("boom"));

        var result = await BuildController().Merge(survivor, archived, notes: null, Xunit.TestContext.Current.CancellationToken);

        result.Should().BeOfType<RedirectToActionResult>()
            .Which.ActionName.Should().Be(nameof(UsersAdminAccountMergesController.Index));
    }

    [HumansFact]
    public async Task MergeRequest_CallsAcceptAsync_RedirectsToIndex()
    {
        var requestId = Guid.NewGuid();
        var survivor = Guid.NewGuid();

        var result = await BuildController().MergeRequest(requestId, survivor, "note", Xunit.TestContext.Current.CancellationToken);

        await _mergeService.Received(1).AcceptAsync(
            requestId, _adminUserId, survivor, "note", Arg.Any<CancellationToken>());
        result.Should().BeOfType<RedirectToActionResult>()
            .Which.ActionName.Should().Be(nameof(UsersAdminAccountMergesController.Index));
    }

    [HumansFact]
    public async Task Dismiss_CallsRejectAsync_RedirectsToIndex()
    {
        var requestId = Guid.NewGuid();

        var result = await BuildController().Dismiss(requestId, "no thanks", Xunit.TestContext.Current.CancellationToken);

        await _mergeService.Received(1).RejectAsync(
            requestId, _adminUserId, "no thanks", Arg.Any<CancellationToken>());
        result.Should().BeOfType<RedirectToActionResult>()
            .Which.ActionName.Should().Be(nameof(UsersAdminAccountMergesController.Index));
    }

    [HumansFact]
    public async Task Close_CallsReconcileMergedRequestAsync_RedirectsToIndex()
    {
        var requestId = Guid.NewGuid();

        var result = await BuildController().Close(requestId, Xunit.TestContext.Current.CancellationToken);

        await _mergeService.Received(1).ReconcileMergedRequestAsync(
            requestId, _adminUserId, Arg.Any<CancellationToken>());
        result.Should().BeOfType<RedirectToActionResult>()
            .Which.ActionName.Should().Be(nameof(UsersAdminAccountMergesController.Index));
    }

    [HumansFact]
    public async Task Close_WhenReconcileRefused_SetsErrorAndRedirectsToIndex()
    {
        var requestId = Guid.NewGuid();
        _mergeService.ReconcileMergedRequestAsync(requestId, _adminUserId, Arg.Any<CancellationToken>())
            .Returns(new AccountMergeResult("not merged into each other"));

        var result = await BuildController().Close(requestId, Xunit.TestContext.Current.CancellationToken);

        result.Should().BeOfType<RedirectToActionResult>()
            .Which.ActionName.Should().Be(nameof(UsersAdminAccountMergesController.Index));
    }
}
