using System.Security.Claims;
using AwesomeAssertions;
using Humans.Camps.Contracts;
using Humans.Governance.Contracts;
using Humans.GoogleIntegration.Contracts;
using Humans.Notifications.Controllers;
using Humans.Notifications.Services;
using Humans.Teams.Contracts;
using Humans.Tickets.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Humans.Notifications.Tests.Controllers;

public sealed class NotificationsControllerTests
{
    [HumansFact]
    public async Task Index_RequestAborted_PropagatesCancellation()
    {
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        var inbox = Substitute.For<INotificationInboxService>();
        inbox.GetInboxAsync(Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string>(), aborted.Token)
            .Returns(Task.FromException<NotificationInboxResult>(new OperationCanceledException(aborted.Token)));
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var meterProvider = new NotificationMeterProvider(
            Substitute.For<IUserServiceRead>(), Substitute.For<IGoogleSyncServiceRead>(),
            Substitute.For<ITeamServiceRead>(), Substitute.For<ITicketSync>(),
            Substitute.For<IApplicationServiceRead>(), Substitute.For<ICampServiceRead>(), cache,
            NullLogger<NotificationMeterProvider>.Instance);
        var controller = new NotificationsController(inbox, Substitute.For<IUserServiceRead>(), meterProvider,
            Substitute.For<IStringLocalizer<NotificationsResource>>())
        {
            ControllerContext = new() { HttpContext = new DefaultHttpContext { RequestAborted = aborted.Token } }
        };
        controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())]));

        var act = () => controller.Index(null);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
