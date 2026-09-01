using System.Reflection;
using AwesomeAssertions;
using Humans.Calendar.Controllers;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Calendar.Tests.Controllers;

/// <summary>
/// Pins the section's authorization posture, which <c>Docs/authorization.md</c> and
/// <c>Docs/Calendar.md</c>'s negative-access rules both assert and nothing enforced.
/// The calendar is deliberately open to any authenticated human — but "authenticated"
/// is the whole gate, so losing <c>[Authorize]</c> exposes every event and every
/// mutation anonymously, and a second <c>[AllowAnonymous]</c> action would be a new
/// unauthenticated surface on a section that is meant to have exactly one.
/// </summary>
public class CalendarAuthorizationTests
{
    [HumansFact]
    public void CalendarController_RequiresAuthentication_AtTheClassLevel()
    {
        typeof(CalendarController)
            .GetCustomAttribute<AuthorizeAttribute>(inherit: false)
            .Should().NotBeNull(because: "every calendar route is authenticated-only");
    }

    [HumansFact]
    public void CalendarController_HasNoAnonymousAction()
    {
        var anonymous = PublicActionsOf(typeof(CalendarController))
            .Where(m => m.GetCustomAttribute<AllowAnonymousAttribute>(inherit: false) is not null)
            .Select(m => m.Name);

        anonymous.Should().BeEmpty(
            because: "the personal iCal feed is the section's only anonymous surface");
    }

    [HumansFact]
    public void ICalFeedApiController_ExposesExactlyOneAnonymousAction()
    {
        var anonymous = PublicActionsOf(typeof(ICalFeedApiController))
            .Where(m => m.GetCustomAttribute<AllowAnonymousAttribute>(inherit: false) is not null)
            .Select(m => m.Name)
            .ToList();

        anonymous.Should().ContainSingle(
            because: "the feed's secret is the token in the URL; nothing else here is anonymous");
    }

    private static IEnumerable<MethodInfo> PublicActionsOf(Type controller) =>
        controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName);
}
