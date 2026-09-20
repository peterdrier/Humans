using System.Security.Claims;
using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Email.Controllers;
using Humans.Email.Services;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Humans.Email.Tests;

/// <summary>
/// Pause/resume post from the /Settings#email tab (peterdrier/Humans#1634), so both
/// actions send the admin back there rather than to the /Email/EmailOutbox dashboard.
/// </summary>
public sealed class EmailControllerPauseSettingsRedirectTests
{
    private readonly IEmailOutboxService _outbox = Substitute.For<IEmailOutboxService>();

    private EmailController BuildSut()
    {
        var controller = new EmailController(
            Substitute.For<IUserServiceRead>(), _outbox, Substitute.For<IAuditLogService>(),
            NullLogger<EmailController>.Instance);
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], "test")),
        };
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        controller.TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>());
        return controller;
    }

    [HumansFact]
    public async Task PauseEmailSending_RedirectsToTheSettingsPageEmailTab()
    {
        var result = await BuildSut().PauseEmailSending();

        result.Should().BeOfType<RedirectResult>().Which.Url.Should().Be("/Settings#email");
    }

    [HumansFact]
    public async Task ResumeEmailSending_RedirectsToTheSettingsPageEmailTab()
    {
        var result = await BuildSut().ResumeEmailSending();

        result.Should().BeOfType<RedirectResult>().Which.Url.Should().Be("/Settings#email");
    }
}
