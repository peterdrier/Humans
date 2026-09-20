using System.Security.Claims;
using System.Text.Json;
using AwesomeAssertions;
using Humans.Backdoor.Contracts;
using Humans.Backdoor.Controllers;
using Humans.Backdoor.Filters;
using Humans.Notifications.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Humans.Backdoor.Tests.Controllers;

/// <summary>
/// The formatting the machine notifications API does on top of
/// <see cref="INotificationInboxRead"/>. Everything below is controller work: the service is
/// a substitute throughout.
/// </summary>
public class BackdoorNotificationsControllerTests
{
    private readonly INotificationInboxRead _inbox = Substitute.For<INotificationInboxRead>();
    private readonly BackdoorNotificationsController _sut;

    public BackdoorNotificationsControllerTests() =>
        _sut = new BackdoorNotificationsController(_inbox, Substitute.For<IUserServiceRead>());

    [HumansFact]
    public void Every_route_hangs_off_the_api_key_filter()
    {
        var filter = typeof(BackdoorNotificationsController)
            .GetCustomAttributes(typeof(ServiceFilterAttribute), inherit: false)
            .Cast<ServiceFilterAttribute>()
            .Single();

        filter.ServiceType.Should().Be(typeof(BackdoorApiKeyAuthFilter));
    }

    [HumansFact]
    public async Task Get_without_a_principal_is_Unauthorized_and_reads_nothing()
    {
        _sut.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };

        var result = await _sut.Get(Xunit.TestContext.Current.CancellationToken);

        result.Should().BeOfType<UnauthorizedResult>();
        await _inbox.DidNotReceiveWithAnyArgs().GetUnreadInboxAsync(default!, default);
    }

    [HumansFact]
    public async Task Get_projects_rows_and_meters_for_the_keys_owner()
    {
        var userId = Guid.NewGuid();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userId.ToString())], BackdoorAuthentication.SchemeName));
        _sut.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = principal } };
        var createdAt = new DateTime(2026, 4, 10, 9, 0, 0, DateTimeKind.Utc);
        _inbox.GetUnreadInboxAsync(principal, Arg.Any<CancellationToken>()).Returns(new NotificationInboxSnapshot(
            [
                new UnreadNotificationDto(
                    createdAt, NotificationSource.ConsentReviewNeeded, "Consent review pending: Alice",
                    "/OnboardingReview", NotificationPriority.High, NotificationClass.Actionable, true),
            ],
            [new NotificationMeterDto("Ticket sync error", 1, "/Tickets")]));

        var result = await _sut.Get(Xunit.TestContext.Current.CancellationToken);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
        var notification = json.RootElement.GetProperty("notifications")[0];
        notification.GetProperty("date").GetDateTime().Should().Be(createdAt);
        notification.GetProperty("source").GetString().Should().Be("ConsentReviewNeeded");
        notification.GetProperty("subject").GetString().Should().Be("Consent review pending: Alice");
        notification.GetProperty("link").GetString().Should().Be("/OnboardingReview");
        notification.GetProperty("priority").GetString().Should().Be("high");
        notification.GetProperty("class").GetString().Should().Be("actionable");
        notification.GetProperty("unread").GetBoolean().Should().BeTrue();

        var meter = json.RootElement.GetProperty("meters")[0];
        meter.GetProperty("label").GetString().Should().Be("Ticket sync error");
        meter.GetProperty("count").GetInt32().Should().Be(1);
        meter.GetProperty("link").GetString().Should().Be("/Tickets");
    }
}
