using System.Security.Claims;
using AwesomeAssertions;
using Humans.Audit.Contracts;
using Humans.Users.Models;
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
    private readonly IAuditViewerService _auditViewer = Substitute.For<IAuditViewerService>();
    private readonly Guid _adminUserId = Guid.NewGuid();

    public UsersAdminAccountMergesControllerTests()
    {
        _mergeService.MergeAsync(default, default, default).ReturnsForAnyArgs(new AccountMergeResult());
        var adminUser = new User { Id = _adminUserId, PreferredLanguage = "en" };
        _userService.GetUserInfoAsync(_adminUserId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(UserInfoFixtures.FromUser(adminUser)));
    }

    private UsersAdminAccountMergesController BuildController()
    {
        var c = new UsersAdminAccountMergesController(
            _userService,
            _mergeService,
            _duplicateService,
            _auditViewer,
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
    public async Task Merge_DependencyFailure_PropagatesWithoutRefusalFeedback()
    {
        var id = Guid.NewGuid();
        var other = Guid.NewGuid();
        var ct = Xunit.TestContext.Current.CancellationToken;
        var failure = new InvalidOperationException("Private dependency failure");
        _mergeService.MergeAsync(id, other, _adminUserId, null, null, ct).Returns(Task.FromException<AccountMergeResult>(failure));
        var controller = BuildController();

        Func<Task> act = () => controller.Merge(id, other, null, ct);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(failure);
        controller.TempData.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Index_PickedPair_ListsItFirst()
    {
        var pickedA = Guid.NewGuid();
        var pickedB = Guid.NewGuid();
        var kept = Guid.NewGuid();
        var mergedAway = Guid.NewGuid();
        _userService.GetAllRawUserInfosAsync(Arg.Any<CancellationToken>()).Returns(
            new[] { pickedA, pickedB }.Select(id => UserInfoFixtures.FromUser(new User { Id = id })).ToList());
        _duplicateService.DetectDuplicatesAsync(Arg.Any<CancellationToken>()).Returns([]);
        var at = NodaTime.Instant.FromUtc(2026, 11, 20, 12, 0);
        AuditEntry Entry(AuditActionDefinition action, Guid actor, NodaTime.Instant when) => new(
            Guid.NewGuid(), when, action, actor, null, nameof(User), mergedAway,
            null, null, null, null, null, kept, nameof(User), "details");
        _auditViewer.GetFilteredAsync(
                nameof(User), null, null, Arg.Any<IReadOnlyList<string>?>(), Arg.Any<int>(), false, Arg.Any<CancellationToken>())
            .Returns([
                Entry(UsersAuditActions.AccountMergeAccepted, kept, at),
                Entry(UsersAuditActions.AccountMergeFailed, _adminUserId, at + NodaTime.Duration.FromMinutes(1)),
            ]);

        var result = await BuildController().Index(pickedA, pickedB, Xunit.TestContext.Current.CancellationToken);

        var model = result.Should().BeOfType<ViewResult>().Which.Model.Should().BeOfType<AccountMergeQueueViewModel>().Subject;
        var row = model.Rows.Should().ContainSingle().Subject;
        row.SharedEmail.Should().BeNull();
        (row.AccountA.UserId, row.AccountB.UserId).Should().Be((pickedA, pickedB));
        // Newest first; a merge done by one of the two accounts is self-service (no "by").
        model.Recent.Select(r => (r.KeptUserId, r.MergedAwayUserId, r.By, r.Failed)).Should().Equal(
            (kept, mergedAway, _adminUserId, true),
            (kept, mergedAway, null, false));
    }

    [HumansFact]
    public async Task Merge_Valid_PassesSurvivorArchivedAdmin()
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
    public async Task Merge_Refused_SetsError()
    {
        var survivor = Guid.NewGuid();
        var archived = Guid.NewGuid();
        _mergeService.MergeAsync(survivor, archived, _adminUserId, Arg.Any<string?>(), null, Arg.Any<CancellationToken>())
            .Returns(new AccountMergeResult("boom"));

        var result = await BuildController().Merge(survivor, archived, notes: null, Xunit.TestContext.Current.CancellationToken);

        result.Should().BeOfType<RedirectToActionResult>()
            .Which.ActionName.Should().Be(nameof(UsersAdminAccountMergesController.Index));
    }
}
