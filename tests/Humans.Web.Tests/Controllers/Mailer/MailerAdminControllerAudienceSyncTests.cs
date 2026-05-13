using System.Security.Claims;
using AwesomeAssertions;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Mailer;
using Humans.Application.Interfaces.Mailer.Dtos;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Web.Controllers.Mailer;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Humans.Web.Tests.Controllers.Mailer;

/// <summary>
/// Verifies <see cref="MailerAdminController.SyncAudience"/>: known key syncs
/// and sets the banner; unknown key returns 404.
/// </summary>
public class MailerAdminControllerAudienceSyncTests
{
    private readonly UserManager<User> _userManager;
    private readonly IMailerImportService _importService = Substitute.For<IMailerImportService>();
    private readonly IMailerLiteService _mlService = Substitute.For<IMailerLiteService>();
    private readonly IMailerAudienceSyncService _audienceSync = Substitute.For<IMailerAudienceSyncService>();
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly ICommunicationPreferenceService _prefs = Substitute.For<ICommunicationPreferenceService>();
    private readonly IAuditLogService _audit = Substitute.For<IAuditLogService>();

    public MailerAdminControllerAudienceSyncTests()
    {
        var userStore = Substitute.For<IUserStore<User>>();
        _userManager = Substitute.For<UserManager<User>>(
            userStore, null, null, null, null, null, null, null, null);
    }

    [HumansFact]
    public async Task SyncAudience_KnownKey_RedirectsWithBanner()
    {
        var audience = Substitute.For<IMailerAudience>();
        audience.Key.Returns("ticket-no-shifts");
        audience.DisplayName.Returns("Ticket holders without a shift");
        audience.MailerLiteGroupName.Returns("Humans - Ticket no Shifts");

        _audienceSync.SyncAsync(audience, Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new AudienceSyncResult(
                "ticket-no-shifts", "g1", "Humans - Ticket no Shifts",
                Candidates: 10, ExcludedUnsubscribed: 1,
                Created: 5, Assigned: 3, AlreadyAssigned: 1, Unassigned: 0, Errors: 0));

        var ctrl = BuildSut(new[] { audience });

        var result = await ctrl.SyncAudience("ticket-no-shifts", CancellationToken.None);

        result.Should().BeOfType<RedirectToActionResult>()
            .Which.ActionName.Should().Be(nameof(MailerAdminController.Index));
        ctrl.TempData["Banner"].Should().NotBeNull();
        ctrl.TempData["Banner"]!.ToString().Should().Contain("Ticket holders without a shift");
    }

    [HumansFact]
    public async Task SyncAudience_UnknownKey_Returns404()
    {
        var ctrl = BuildSut(Array.Empty<IMailerAudience>());

        var result = await ctrl.SyncAudience("nope", CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    [HumansFact]
    public async Task SyncAudience_SyncThrowsInvalidOperation_RedirectsWithErrorBanner()
    {
        var audience = Substitute.For<IMailerAudience>();
        audience.Key.Returns("bad-audience");
        audience.DisplayName.Returns("Bad");
        audience.MailerLiteGroupName.Returns("Bad");
        _audienceSync.SyncAsync(audience, Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns<Task<AudienceSyncResult>>(_ => throw new InvalidOperationException("prefix violation"));

        var ctrl = BuildSut(new[] { audience });

        var result = await ctrl.SyncAudience("bad-audience", CancellationToken.None);

        result.Should().BeOfType<RedirectToActionResult>();
        ctrl.TempData["Banner"]!.ToString().Should().Contain("sync failed");
    }

    private MailerAdminController BuildSut(IEnumerable<IMailerAudience> audiences)
    {
        var ctrl = new MailerAdminController(
            _mlService, _importService, _audienceSync, audiences,
            _userService, _prefs, _audit,
            NullLogger<MailerAdminController>.Instance, _userManager);

        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()) },
                "test")),
        };
        ctrl.ControllerContext = new ControllerContext { HttpContext = http };
        ctrl.TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>());

        return ctrl;
    }
}
