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

    private static MonitorController BuildSut()
    {
        var controller = new MonitorController(
            Substitute.For<IUserServiceRead>(),
            NullLogger<MonitorController>.Instance);
        var http = new DefaultHttpContext();
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        controller.TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>());
        return controller;
    }
}
