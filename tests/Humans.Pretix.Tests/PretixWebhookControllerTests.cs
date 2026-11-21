using AwesomeAssertions;
using Humans.Pretix.Controllers;
using Humans.Tickets.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Humans.Pretix.Tests;

public class PretixWebhookControllerTests
{
    private readonly ITicketCheckoutEvents _events = Substitute.For<ITicketCheckoutEvents>();

    private PretixWebhookController Controller(string secret = "s3cret")
    {
        var settings = PretixTestHost.Settings();
        settings.WebhookSecret = secret;
        return new PretixWebhookController(_events, Options.Create(settings), NullLogger<PretixWebhookController>.Instance);
    }

    private static PretixWebhookController.PretixNotification Paid(string organizer = "nobodies") =>
        new(organizer, "nowhere2027", "ABC23", "pretix.event.order.paid");

    [HumansFact]
    public async Task MatchingSecret_NudgesTicketsWithTheBackendOrderId()
    {
        var result = await Controller().Receive("s3cret", Paid(), TestContext.Current.CancellationToken);

        result.Should().BeOfType<OkResult>();
        await _events.Received(1).CheckoutChangedAsync("pretix", "nowhere2027/ABC23", Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task WrongSecret_IsNotFoundAndNudgesNothing()
    {
        var result = await Controller().Receive("guess", Paid(), TestContext.Current.CancellationToken);

        result.Should().BeOfType<NotFoundResult>();
        await _events.DidNotReceiveWithAnyArgs().CheckoutChangedAsync(default!, default!, default);
    }

    [HumansFact]
    public async Task UnsetSecret_Is503()
    {
        var result = await Controller(secret: "").Receive("", Paid(), TestContext.Current.CancellationToken);

        result.Should().BeOfType<StatusCodeResult>().Which.StatusCode.Should().Be(503);
    }

    [HumansFact]
    public async Task AnotherOrganizer_IsAcknowledgedAndIgnored()
    {
        var result = await Controller().Receive("s3cret", Paid("someone-else"), TestContext.Current.CancellationToken);

        result.Should().BeOfType<OkResult>();
        await _events.DidNotReceiveWithAnyArgs().CheckoutChangedAsync(default!, default!, default);
    }
}
