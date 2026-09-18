using AwesomeAssertions;
using Humans.GoogleIntegration.Contracts;
using Humans.GoogleIntegration.Controllers;
using Humans.GoogleIntegration.Services;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Mvc;
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
        var result = await BuildSut().Resource(Guid.NewGuid());

        result.Should().BeOfType<NotFoundResult>();
    }

    [HumansFact]
    public async Task Human_WithUnknownId_ReturnsNotFound()
    {
        var result = await BuildSut().Human(Guid.NewGuid());

        result.Should().BeOfType<NotFoundResult>();
    }

    private GoogleController BuildSut() => new(
        _users,
        Substitute.For<IGoogleSyncService>(),
        Substitute.For<IGoogleGroupSync>(),
        _resources,
        Substitute.For<IEmailProvisioningService>(),
        Substitute.For<IGoogleAdminService>(),
        NullLogger<GoogleController>.Instance);
}
