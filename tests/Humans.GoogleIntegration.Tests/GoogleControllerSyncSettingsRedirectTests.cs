using AwesomeAssertions;
using Humans.GoogleIntegration.Contracts;
using Humans.GoogleIntegration.Controllers;
using Humans.GoogleIntegration.Services;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Humans.GoogleIntegration.Tests;

/// <summary>
/// Superseded by the /Settings#google-sync tab (peterdrier/Humans#1634) — one canonical
/// URL per page, so a GET here always redirects rather than staying a second live page.
/// </summary>
public sealed class GoogleControllerSyncSettingsRedirectTests
{
    [HumansFact]
    public void SyncSettings_Get_RedirectsToTheSettingsPageGoogleSyncTab()
    {
        var controller = new GoogleController(
            Substitute.For<IUserServiceRead>(),
            Substitute.For<IGoogleSyncService>(),
            Substitute.For<IGoogleGroupSync>(),
            Substitute.For<ITeamResourceService>(),
            Substitute.For<IEmailProvisioningService>(),
            Substitute.For<IGoogleAdminService>(),
            NullLogger<GoogleController>.Instance);

        var result = controller.SyncSettings();

        result.Should().BeOfType<RedirectResult>().Which.Url.Should().Be("/Settings#google-sync");
    }
}
