using System.Security.Claims;
using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.MailerLite.Controllers;
using Humans.MailerLite.Services;
using Humans.MailerLite.Services.Dtos;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Humans.MailerLite.Tests.Controllers;

/// <summary>
/// Verifies <see cref="MailerLiteAdminController.SyncAudience"/>: known key syncs
/// and sets the banner; unknown key returns 404.
/// </summary>
public class MailerLiteAdminControllerAudienceSyncTests
{
    private readonly IMailerLiteImportService _importService = Substitute.For<IMailerLiteImportService>();
    private readonly IMailerLiteService _mlService = Substitute.For<IMailerLiteService>();
    private readonly IMailerLiteAudienceSyncService _audienceSync = Substitute.For<IMailerLiteAudienceSyncService>();
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly ICommunicationPreferenceService _prefs = Substitute.For<ICommunicationPreferenceService>();
    private readonly IAuditLogService _audit = Substitute.For<IAuditLogService>();

    [HumansFact]
    public async Task SyncAudience_KnownKey_RedirectsWithBanner()
    {
        var audience = Substitute.For<IMailerLiteAudience>();
        audience.Key.Returns("ticket-no-shifts");
        audience.DisplayName.Returns("Ticket holders without a shift");
        audience.MailerLiteGroupName.Returns("Humans - Ticket no Shifts");

        _audienceSync.SyncAsync(audience, Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new AudienceSyncResult(
                "ticket-no-shifts", "g1", "Humans - Ticket no Shifts",
                Candidates: 10, ExcludedUnsubscribed: 1,
                Created: 5, Assigned: 3, AlreadyAssigned: 1, Unassigned: 0, Errors: 0));

        var ctrl = BuildSut([audience]);

        var result = await ctrl.SyncAudience("ticket-no-shifts");

        result.Should().BeOfType<RedirectToActionResult>()
            .Which.ActionName.Should().Be(nameof(MailerLiteAdminController.Index));
        ctrl.TempData["Banner"].Should().NotBeNull();
        ctrl.TempData["Banner"]!.ToString().Should().Contain("Ticket holders without a shift");
    }

    [HumansFact]
    public async Task SyncAudience_UnknownKey_Returns404()
    {
        var ctrl = BuildSut([]);

        var result = await ctrl.SyncAudience("nope");

        result.Should().BeOfType<NotFoundResult>();
    }

    [HumansFact]
    public async Task SyncAudience_SyncThrowsInvalidOperation_RedirectsWithErrorBanner()
    {
        var audience = Substitute.For<IMailerLiteAudience>();
        audience.Key.Returns("bad-audience");
        audience.DisplayName.Returns("Bad");
        audience.MailerLiteGroupName.Returns("Bad");
        _audienceSync.SyncAsync(audience, Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns<Task<AudienceSyncResult>>(_ => throw new InvalidOperationException("prefix violation"));

        var ctrl = BuildSut([audience]);

        var result = await ctrl.SyncAudience("bad-audience");

        result.Should().BeOfType<RedirectToActionResult>();
        ctrl.TempData["Banner"]!.ToString().Should().Contain("sync failed");
    }

    private MailerLiteAdminController BuildSut(IEnumerable<IMailerLiteAudience> audiences)
    {
        var ctrl = new MailerLiteAdminController(
            _mlService, _importService, _audienceSync, audiences,
            _userService, _prefs, _audit,
            NullLogger<MailerLiteAdminController>.Instance);

        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())
                ],
                "test")),
        };
        ctrl.ControllerContext = new ControllerContext { HttpContext = http };
        ctrl.TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>());

        return ctrl;
    }
}
