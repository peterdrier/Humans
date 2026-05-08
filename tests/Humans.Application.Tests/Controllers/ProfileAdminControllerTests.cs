using System.Security.Claims;
using AwesomeAssertions;
using Humans.Application.DTOs.EmailProblems;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Testing;
using Humans.Web.Authorization;
using Humans.Web.Controllers;
using Humans.Web.Models.EmailProblems;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Humans.Application.Tests.Controllers;

public class ProfileAdminControllerTests
{
    private readonly IEmailProblemsService _emailProblems = Substitute.For<IEmailProblemsService>();
    private readonly IAccountMergeService _accountMerge = Substitute.For<IAccountMergeService>();
    private readonly IUserEmailService _userEmails = Substitute.For<IUserEmailService>();
    private readonly IUserService _users = Substitute.For<IUserService>();
    private readonly IProfileService _profileService = Substitute.For<IProfileService>();
    private readonly ITeamService _teamService = Substitute.For<ITeamService>();
    private readonly IRoleAssignmentService _roleAssignmentService = Substitute.For<IRoleAssignmentService>();
    private readonly IAuditLogService _audit = Substitute.For<IAuditLogService>();
    private readonly UserManager<User> _userManager;
    private readonly Guid _adminUserId = Guid.NewGuid();
    private readonly User _adminUser;

    public ProfileAdminControllerTests()
    {
        _adminUser = new User { Id = _adminUserId };
        var userStore = Substitute.For<IUserStore<User>>();
        _userManager = Substitute.For<UserManager<User>>(
            userStore, null, null, null, null, null, null, null, null);
        _userManager.GetUserAsync(Arg.Any<ClaimsPrincipal>()).Returns(_adminUser);
    }

    private ProfileAdminController BuildController()
    {
        var c = new ProfileAdminController(
            _userManager,
            _emailProblems,
            _accountMerge,
            _userEmails,
            _users,
            _audit,
            NullLogger<ProfileAdminController>.Instance,
            _profileService,
            _teamService,
            _roleAssignmentService);

        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, _adminUserId.ToString()),
        }, authenticationType: "TestAuth");
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
        var attr = typeof(ProfileAdminController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .Cast<AuthorizeAttribute>()
            .Single();
        attr.Policy.Should().Be(PolicyNames.AdminOnly);
    }

    [HumansFact]
    public async Task EmailProblems_GET_CallsScanAndReturnsViewWithModel()
    {
        _emailProblems.ScanAsync(Arg.Any<CancellationToken>())
            .Returns(new EmailProblemsReport(NodaTime.SystemClock.Instance.GetCurrentInstant(),
                Array.Empty<EmailProblem>()));
        _users.GetByIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, User>());

        var result = await BuildController().EmailProblems(default);

        result.Should().BeOfType<ViewResult>()
            .Which.Model.Should().BeOfType<EmailProblemsListViewModel>();
        await _emailProblems.Received(1).ScanAsync(Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Merge_TargetEqualsUser1_CallsAdminMergeWithUser2AsSource()
    {
        var u1 = Guid.NewGuid();
        var u2 = Guid.NewGuid();
        _emailProblems.UsersShareAnyEmailAsync(u1, u2, Arg.Any<CancellationToken>()).Returns(true);

        var result = await BuildController().Merge(u1, u2, targetUserId: u1, notes: null, ct: default);

        await _accountMerge.Received(1).AdminMergeAsync(u2, u1, _adminUserId, null, Arg.Any<CancellationToken>());
        result.Should().BeOfType<RedirectToActionResult>()
            .Which.ActionName.Should().Be(nameof(ProfileAdminController.EmailProblems));
    }

    [HumansFact]
    public async Task Merge_TargetNotInPair_RedirectsToCompareWithError_NoMergeCall()
    {
        var u1 = Guid.NewGuid();
        var u2 = Guid.NewGuid();
        var stranger = Guid.NewGuid();

        var result = await BuildController().Merge(u1, u2, targetUserId: stranger, notes: null, ct: default);

        await _accountMerge.DidNotReceive().AdminMergeAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        result.Should().BeOfType<RedirectToActionResult>()
            .Which.ActionName.Should().Be(nameof(ProfileAdminController.EmailProblemsCompare));
    }

    [HumansFact]
    public async Task Merge_UsersDoNotShareEmail_RedirectsToList_NoMergeCall()
    {
        var u1 = Guid.NewGuid();
        var u2 = Guid.NewGuid();
        _emailProblems.UsersShareAnyEmailAsync(u1, u2, Arg.Any<CancellationToken>()).Returns(false);

        var result = await BuildController().Merge(u1, u2, targetUserId: u1, notes: null, ct: default);

        await _accountMerge.DidNotReceive().AdminMergeAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        result.Should().BeOfType<RedirectToActionResult>()
            .Which.ActionName.Should().Be(nameof(ProfileAdminController.EmailProblems));
    }

    [HumansFact]
    public async Task DeleteOrphanEmail_RowExists_AuditsAndRedirects()
    {
        var emailId = Guid.NewGuid();
        _userEmails.DeleteByIdAsync(emailId, Arg.Any<CancellationToken>()).Returns(true);

        var result = await BuildController().DeleteOrphanEmail(emailId, default);

        await _audit.Received(1).LogAsync(
            AuditAction.OrphanUserEmailDeleted,
            nameof(UserEmail), emailId,
            Arg.Any<string>(),
            _adminUserId);
        result.Should().BeOfType<RedirectToActionResult>();
    }

    [HumansFact]
    public async Task DeleteOrphanEmail_RowGone_NoAudit()
    {
        var emailId = Guid.NewGuid();
        _userEmails.DeleteByIdAsync(emailId, Arg.Any<CancellationToken>()).Returns(false);

        var result = await BuildController().DeleteOrphanEmail(emailId, default);

        await _audit.DidNotReceive().LogAsync(
            Arg.Any<AuditAction>(), Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<string>(), Arg.Any<Guid>());
        result.Should().BeOfType<RedirectToActionResult>();
    }

    [HumansFact]
    public async Task DeleteGhostLogins_RowsDeleted_AuditsWithCount()
    {
        var userId = Guid.NewGuid();
        _emailProblems.IsGhostExternalLoginsUserAsync(userId, Arg.Any<CancellationToken>()).Returns(true);
        _users.DeleteAllExternalLoginsForUserAsync(userId, Arg.Any<CancellationToken>()).Returns(3);

        var result = await BuildController().DeleteGhostLogins(userId, default);

        await _audit.Received(1).LogAsync(
            AuditAction.GhostExternalLoginsDeleted, nameof(User), userId,
            Arg.Is<string>(s => s.Contains("3")),
            _adminUserId);
        result.Should().BeOfType<RedirectToActionResult>();
    }

    [HumansFact]
    public async Task BackfillLegacyEmails_AuditsEachReturnedRow()
    {
        var u1 = Guid.NewGuid();
        var u2 = Guid.NewGuid();
        _emailProblems.BackfillLegacyIdentityEmailsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<(Guid, string)> { (u1, "a@x.com"), (u2, "b@x.com") });

        var result = await BuildController().BackfillLegacyEmails(default);

        await _audit.Received(1).LogAsync(
            AuditAction.LegacyIdentityEmailBackfilled, nameof(User), u1,
            Arg.Is<string>(s => s.Contains("a@x.com")),
            _adminUserId);
        await _audit.Received(1).LogAsync(
            AuditAction.LegacyIdentityEmailBackfilled, nameof(User), u2,
            Arg.Is<string>(s => s.Contains("b@x.com")),
            _adminUserId);
        result.Should().BeOfType<RedirectToActionResult>()
            .Which.ActionName.Should().Be(nameof(ProfileAdminController.EmailProblems));
    }

    [HumansFact]
    public async Task BackfillLegacyEmails_NoneToBackfill_NoAudit()
    {
        _emailProblems.BackfillLegacyIdentityEmailsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<(Guid, string)>());

        var result = await BuildController().BackfillLegacyEmails(default);

        await _audit.DidNotReceive().LogAsync(
            Arg.Any<AuditAction>(), Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<string>(), Arg.Any<Guid>());
        result.Should().BeOfType<RedirectToActionResult>();
    }

    [HumansFact]
    public async Task DeleteGhostLogins_NotInGhostSet_NoOpsAndNoAudit()
    {
        var userId = Guid.NewGuid();
        _emailProblems.IsGhostExternalLoginsUserAsync(userId, Arg.Any<CancellationToken>()).Returns(false);

        var result = await BuildController().DeleteGhostLogins(userId, default);

        await _users.DidNotReceive().DeleteAllExternalLoginsForUserAsync(
            Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _audit.DidNotReceive().LogAsync(
            Arg.Any<AuditAction>(), Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<string>(), Arg.Any<Guid>());
        result.Should().BeOfType<RedirectToActionResult>();
    }
}
