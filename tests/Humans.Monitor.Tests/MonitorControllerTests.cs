using System.Security.Claims;
using AwesomeAssertions;
using Humans.Base.Constants;
using Humans.Monitor.Contracts;
using Humans.Monitor.Controllers;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Humans.Monitor.Tests;

public sealed class MonitorControllerTests
{
    [HumansFact]
    public async Task CheckDriveActivity_WhenScanThrows_ShowsErrorAndRedirects()
    {
        var monitor = Substitute.For<IDriveActivityMonitorService>();
        monitor.CheckForAnomalousActivityAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Google unavailable"));
        var controller = BuildSut();

        var result = await controller.CheckDriveActivity(monitor);

        var redirect = result.Should().BeOfType<RedirectToActionResult>().Subject;
        redirect.ActionName.Should().Be("Index");
        redirect.ControllerName.Should().Be("AuditLog");
        controller.TempData[TempDataKeys.ErrorMessage]
            .Should().Be("Drive activity check failed. Check logs for details.");
    }

    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task Manual_scan_cancels_only_the_account_preflight(bool cancelBeforeLookup)
    {
        var userId = Guid.NewGuid();
        var users = Substitute.For<IUserService>();
        var monitor = Substitute.For<IDriveActivityMonitorService>();
        using var request = new CancellationTokenSource();
        users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(call =>
        {
            call.Arg<CancellationToken>().Should().Be(request.Token);
            call.Arg<CancellationToken>().ThrowIfCancellationRequested();
            return new ValueTask<UserInfo?>(UserInfo.Create(new User { Id = userId }, [], [], null));
        });
        monitor.CheckForAnomalousActivityAsync(Arg.Any<CancellationToken>()).Returns(async call =>
        {
            call.Arg<CancellationToken>().Should().Be(CancellationToken.None);
            await request.CancelAsync();
            return 0;
        });
        var controller = BuildSut(users);
        controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test"));
        controller.HttpContext.RequestAborted = request.Token;
        if (cancelBeforeLookup)
            await request.CancelAsync();
        var scan = () => controller.CheckDriveActivity(monitor);

        if (cancelBeforeLookup)
        {
            await scan.Should().ThrowAsync<OperationCanceledException>();
            monitor.ReceivedCalls().Should().BeEmpty();
            controller.TempData.Should().BeEmpty();
        }
        else
        {
            (await scan()).Should().BeOfType<RedirectToActionResult>();
            controller.TempData[TempDataKeys.SuccessMessage]
                .Should().Be("Drive activity check completed: no anomalies detected.");
        }
    }

    private static MonitorController BuildSut(IUserService? users = null)
    {
        var controller = new MonitorController(
            users ?? Substitute.For<IUserService>(),
            NullLogger<MonitorController>.Instance);
        var http = new DefaultHttpContext();
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        controller.TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>());
        return controller;
    }
}
