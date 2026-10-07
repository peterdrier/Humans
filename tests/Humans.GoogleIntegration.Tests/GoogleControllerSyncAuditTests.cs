using AwesomeAssertions;
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
