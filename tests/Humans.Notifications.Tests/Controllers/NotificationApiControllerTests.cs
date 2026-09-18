using System.Security.Claims;
using System.Text.Json;
using AwesomeAssertions;
using Humans.Base.Constants;
using Humans.Camps.Contracts;
using Humans.GoogleIntegration.Contracts;
using Humans.Governance.Contracts;
using Humans.Notifications.Contracts;
using Humans.Notifications.Controllers;
using Humans.Notifications.Services;
using Humans.Teams.Contracts;
using Humans.Tickets.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Humans.Notifications.Tests.Controllers;

public sealed class NotificationApiControllerTests : IDisposable
{
    private readonly INotificationInboxService _inbox = Substitute.For<INotificationInboxService>();
    private readonly IUserServiceRead _users = Substitute.For<IUserServiceRead>();
    private readonly IGoogleSyncServiceRead _google = Substitute.For<IGoogleSyncServiceRead>();
    private readonly ITeamServiceRead _teams = Substitute.For<ITeamServiceRead>();
    private readonly ITicketSync _tickets = Substitute.For<ITicketSync>();
    private readonly IApplicationServiceRead _applications = Substitute.For<IApplicationServiceRead>();
    private readonly ICampServiceRead _camps = Substitute.For<ICampServiceRead>();
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());

    public void Dispose()
    {
        _cache.Dispose();
        GC.SuppressFinalize(this);
    }

    [HumansFact]
    public void Controller_is_gated_by_the_notification_api_key_filter()
    {
        var filter = typeof(NotificationApiController)
            .GetCustomAttributes(typeof(ServiceFilterAttribute), inherit: false)
            .Cast<ServiceFilterAttribute>()
            .Single();

        filter.ServiceType.Should().Be(
            typeof(Humans.Notifications.Filters.NotificationApiKeyAuthFilter));
    }

    [HumansFact]
    public async Task Get_returns_unread_notifications_and_meters_without_mutating_inbox()
    {
        var userId = Guid.NewGuid();
        var createdAt = new DateTime(2026, 4, 10, 9, 0, 0, DateTimeKind.Utc);
        _inbox.GetInboxAsync(
                userId, null, "all", "unread", Arg.Any<CancellationToken>())
            .Returns(new NotificationInboxResult
            {
                NeedsAttention =
                [
                    new NotificationRowDto
                    {
                        Title = "Consent review pending: Alice",
                        ActionUrl = "/OnboardingReview",
                        Priority = NotificationPriority.High,
                        Source = NotificationSource.ConsentReviewNeeded,
                        Class = NotificationClass.Actionable,
                        CreatedAt = createdAt,
                        IsRead = false,
                    },
                ],
            });
        StubMeterDependencies();
        _tickets.IsInErrorStateAsync(Arg.Any<CancellationToken>()).Returns(true);

        var controller = Controller(userId, RoleNames.Admin);

        var result = await controller.Get(Xunit.TestContext.Current.CancellationToken);

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

        await _inbox.DidNotReceiveWithAnyArgs().MarkReadAsync(default, default, default);
        await _inbox.DidNotReceiveWithAnyArgs().MarkAllReadAsync(default, default);
        await _inbox.DidNotReceiveWithAnyArgs().ResolveAsync(default, default, default);
        await _inbox.DidNotReceiveWithAnyArgs().DismissAsync(default, default, default);
    }

    private NotificationApiController Controller(Guid userId, params string[] roles)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId.ToString()) };
        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            claims, Humans.Notifications.Filters.NotificationApiKeyAuthFilter.AuthenticationScheme));

        return new NotificationApiController(
            _inbox,
            new NotificationMeterProvider(
                _users, _google, _teams, _tickets, _applications, _camps, _cache,
                NullLogger<NotificationMeterProvider>.Instance),
            _users)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = principal },
            },
        };
    }

    private void StubMeterDependencies()
    {
        _users.GetAllUserInfosAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyCollection<UserInfo>>([]));
        _google.GetFailedSyncEventCountAsync(Arg.Any<CancellationToken>()).Returns(0);
        _teams.GetTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<Guid, TeamInfo>>(
                new Dictionary<Guid, TeamInfo>()));
        _tickets.IsInErrorStateAsync(Arg.Any<CancellationToken>()).Returns(false);
        _camps.GetSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(new CampSettingsInfo(2026, [], null));
        _camps.GetCampsForYearAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<CampInfo>>([]));
    }
}
