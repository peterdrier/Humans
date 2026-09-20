using AwesomeAssertions;
using Humans.Events.Controllers;
using Humans.Events.Services;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Humans.Events.Tests.Controllers;

/// <summary>
/// <c>GET /Events/Admin/Settings</c> is superseded by the /Settings#event-guide tab
/// (peterdrier/Humans#1634) — one canonical URL per page.
/// </summary>
public sealed class EventsAdminControllerSettingsTests
{
    [HumansFact]
    public void Settings_Get_RedirectsToTheSettingsPageEventGuideTab()
    {
        var controller = new EventsAdminController(
            Substitute.For<IEventService>(),
            NullLogger<EventsAdminController>.Instance,
            Substitute.For<IUserServiceRead>());

        var result = controller.Settings();

        result.Should().BeOfType<RedirectResult>().Which.Url.Should().Be("/Settings#event-guide");
    }
}
