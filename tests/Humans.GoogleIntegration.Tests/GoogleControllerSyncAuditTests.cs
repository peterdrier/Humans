using AwesomeAssertions;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Xunit;
using Humans.GoogleIntegration.Contracts;
using Humans.GoogleIntegration.Controllers;
using Humans.GoogleIntegration.Services;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Humans.GoogleIntegration.Tests;

public sealed class GoogleControllerSyncAuditTests
{
    private readonly IUserServiceRead _users = Substitute.For<IUserServiceRead>();
    private readonly ITeamResourceService _resources = Substitute.For<ITeamResourceService>();

    [HumansFact]
    public async Task Resource_WithUnknownId_ReturnsNotFound()
    {
        using var cancellation = new CancellationTokenSource();
        var id = Guid.NewGuid();
        var result = await BuildSut(cancellation.Token).Resource(id);

        result.Should().BeOfType<NotFoundResult>();
        await _resources.Received(1).GetResourceByIdAsync(id, cancellation.Token);
    }

    [HumansFact]
    public async Task Human_WithUnknownId_ReturnsNotFound()
    {
        using var cancellation = new CancellationTokenSource();
        var id = Guid.NewGuid();
        var result = await BuildSut(cancellation.Token).Human(id);

        result.Should().BeOfType<NotFoundResult>();
        await _users.Received(1).GetUserInfoAsync(id, cancellation.Token);
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpdateSyncSetting_OnlyValidBindingsReachTheActorAttributedWrite(bool malformed)
    {
        var actorId = Guid.NewGuid();
        var controller = BuildSut(Xunit.TestContext.Current.CancellationToken);
        controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, actorId.ToString())], "test"));
        var actor = UserInfo.Create(new User { Id = actorId, BurnerName = "Admin" }, [], [], [], null, []);
        _users.GetUserInfoAsync(actorId, Arg.Any<CancellationToken>()).Returns(actor);
        controller.TempData = new TempDataDictionary(controller.HttpContext, Substitute.For<ITempDataProvider>());
        var settings = Substitute.For<ISyncSettingsService>();
        if (malformed)
            controller.ModelState.AddModelError("mode", "Not a sync mode.");

        var result = await controller.UpdateSyncSetting(settings, SyncServiceType.GoogleDrive, SyncMode.None);

        if (malformed)
        {
            result.Should().BeOfType<BadRequestObjectResult>();
            settings.ReceivedCalls().Should().BeEmpty();
        }
        else
        {
            result.Should().BeOfType<RedirectResult>().Which.Url.Should().Be("/Settings#google-sync");
            await settings.Received(1).UpdateModeAsync(SyncServiceType.GoogleDrive, SyncMode.None,
                actorId, Arg.Any<CancellationToken>());
        }
    }

    private GoogleController BuildSut(CancellationToken token) => new(
        _users,
        Substitute.For<IGoogleSyncService>(),
        Substitute.For<IGoogleGroupSync>(),
        _resources,
        Substitute.For<IEmailProvisioningService>(),
        Substitute.For<IGoogleAdminService>(),
        NullLogger<GoogleController>.Instance)
    {
        ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestAborted = token } }
    };
}
