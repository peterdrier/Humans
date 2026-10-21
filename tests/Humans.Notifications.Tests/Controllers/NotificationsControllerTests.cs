using Humans.Consent.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
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
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
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
        using var localization = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider();
        var meterProvider = new NotificationMeterProvider(
            Substitute.For<IUserService>(), Substitute.For<IConsentService>(), Substitute.For<IGoogleSyncService>(),
            Substitute.For<ITeamService>(), Substitute.For<ITicketSync>(),
            Substitute.For<IGovernanceService>(), Substitute.For<ICampService>(), cache,
            NullLogger<NotificationMeterProvider>.Instance,
            localization.GetRequiredService<IStringLocalizer<NotificationsResource>>());
        var controller = new NotificationsController(inbox, Substitute.For<IUserService>(), meterProvider, NullLogger<NotificationsController>.Instance)
        {
            ControllerContext = new() { HttpContext = new DefaultHttpContext { RequestAborted = aborted.Token } }
        };
        controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())]));

        var act = () => controller.Index(null);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
    [HumansTheory]
    [Xunit.InlineData(true, true)]
    [Xunit.InlineData(false, true)]
    [Xunit.InlineData(true, false)]
    [Xunit.InlineData(false, false)]
    public async Task BulkActions_RejectMalformedSelectionsBeforeMutating(bool resolve, bool authenticated)
    {
        var userId = Guid.NewGuid();
        var inbox = Substitute.For<INotificationInboxService>();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        using var localization = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider();
        var meterProvider = new NotificationMeterProvider(
            Substitute.For<IUserService>(), Substitute.For<IConsentService>(), Substitute.For<IGoogleSyncService>(),
            Substitute.For<ITeamService>(), Substitute.For<ITicketSync>(),
            Substitute.For<IGovernanceService>(), Substitute.For<ICampService>(), cache,
            NullLogger<NotificationMeterProvider>.Instance,
            localization.GetRequiredService<IStringLocalizer<NotificationsResource>>());
        var context = new DefaultHttpContext();
        if (authenticated)
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test"));
        var controller = new NotificationsController(inbox, Substitute.For<IUserService>(), meterProvider, NullLogger<NotificationsController>.Instance)
        {
            ControllerContext = new() { HttpContext = context }
        };
        controller.ModelState.AddModelError("selectedIds[1]", "Invalid identifier");
        var selected = new List<Guid> { Guid.NewGuid(), Guid.Empty };

        var result = resolve
            ? await controller.BulkResolve(selected)
            : await controller.BulkDismiss(selected);

        if (authenticated)
            result.Should().BeOfType<BadRequestObjectResult>();
        else
            result.Should().BeOfType<UnauthorizedResult>();
        await inbox.DidNotReceive().BulkResolveAsync(Arg.Any<List<Guid>>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await inbox.DidNotReceive().BulkDismissAsync(Arg.Any<List<Guid>>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

}
