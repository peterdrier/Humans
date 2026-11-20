using System.Security.Claims;
using AwesomeAssertions;
using Humans.Notifications.Contracts;
using Humans.Notifications.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Humans.Notifications.Tests.Services;

/// <summary>
/// The composition <see cref="NotificationInboxRead"/> does for the machine API: the unread
/// tab of the inbox service, newest first, plus the viewer's meters, highest priority first.
/// The inbox service and the meter contributor are substitutes; the meter provider is real.
/// </summary>
public sealed class NotificationInboxReadTests
{
    private readonly INotificationInboxService _inbox = Substitute.For<INotificationInboxService>();
    private readonly INotificationMeterContributor _contributor = Substitute.For<INotificationMeterContributor>();
    private readonly NotificationInboxRead _sut;

    public NotificationInboxReadTests()
    {
        _contributor.GetMetersAsync(default!, default, default).ReturnsForAnyArgs([]);
        _sut = new NotificationInboxRead(
            _inbox, new NotificationMeterProvider([_contributor], NullLogger<NotificationMeterProvider>.Instance));
    }

    [HumansFact]
    public async Task Rows_come_from_the_unread_tab_across_both_lists()
    {
        var userId = Guid.NewGuid();
        var older = new DateTime(2026, 4, 9, 9, 0, 0, DateTimeKind.Utc);
        var newer = new DateTime(2026, 4, 10, 9, 0, 0, DateTimeKind.Utc);
        _inbox.GetInboxAsync(userId, null, "all", "unread", Arg.Any<CancellationToken>())
            .Returns(new NotificationInboxResult
            {
                NeedsAttention =
                [
                    new NotificationRowDto
                    {
                        Title = "Consent review pending: Alice",
                        ActionUrl = "/Consent/ReviewQueue",
                        Priority = NotificationPriority.High,
                        Source = TestSources.Plain.Name,
                        Class = NotificationClass.Actionable,
                        CreatedAt = older,
                    },
                ],
                Informational =
                [
                    new NotificationRowDto
                    {
                        Title = "Welcome to Ice Crew",
                        Priority = NotificationPriority.Normal,
                        Source = TestSources.TeamUpdate.Name,
                        Class = NotificationClass.Informational,
                        CreatedAt = newer,
                        IsRead = true,
                    },
                ],
            });

        var snapshot = await _sut.GetUnreadInboxAsync(Principal(userId), Xunit.TestContext.Current.CancellationToken);

        snapshot.Notifications.Should().BeEquivalentTo(new[]
        {
            new UnreadNotificationDto(newer, TestSources.TeamUpdate.Name, "Welcome to Ice Crew", null,
                NotificationPriority.Normal, NotificationClass.Informational, false),
            new UnreadNotificationDto(older, TestSources.Plain.Name, "Consent review pending: Alice", "/Consent/ReviewQueue",
                NotificationPriority.High, NotificationClass.Actionable, true),
        });
        snapshot.Meters.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Meters_are_the_viewers_contributed_ones_with_priority_metadata()
    {
        var userId = Guid.NewGuid();
        var principal = Principal(userId);
        _inbox.GetInboxAsync(userId, null, "all", "unread", Arg.Any<CancellationToken>())
            .Returns(new NotificationInboxResult());
        _contributor.GetMetersAsync(principal, userId, Arg.Any<CancellationToken>()).Returns(
        [
            new NotificationMeter { Title = "Ticket sync error", Count = 1, ActionUrl = "/Tickets", Audience = "Admins", Priority = 4 },
            new NotificationMeter { Title = "Failed Google sync events", Count = 2, ActionUrl = "/Google/SyncOutbox", Audience = "Admins", Priority = 7 },
        ]);

        var snapshot = await _sut.GetUnreadInboxAsync(principal, Xunit.TestContext.Current.CancellationToken);

        snapshot.Meters.Should().BeEquivalentTo(new[]
        {
            new NotificationMeterDto("Failed Google sync events", 2, "/Google/SyncOutbox") { Priority = 7 },
            new NotificationMeterDto("Ticket sync error", 1, "/Tickets") { Priority = 4 },
        }, options => options.WithStrictOrdering());
    }

    [HumansFact]
    public async Task A_principal_without_a_user_id_gets_no_rows_and_no_inbox_query()
    {
        var snapshot = await _sut.GetUnreadInboxAsync(new ClaimsPrincipal(new ClaimsIdentity()), Xunit.TestContext.Current.CancellationToken);

        snapshot.Notifications.Should().BeEmpty();
        await _inbox.DidNotReceiveWithAnyArgs().GetInboxAsync(default, default, default!, default!);
    }

    private static ClaimsPrincipal Principal(Guid userId) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], authenticationType: "Test"));
}
