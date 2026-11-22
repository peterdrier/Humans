using System.Security.Cryptography;
using System.Text;
using AwesomeAssertions;
using Humans.HiEvents.Controllers;
using Humans.HiEvents.Services;
using Humans.Tickets.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Humans.HiEvents.Tests;

public class HiEventsWebhookControllerTests
{
    private const string Secret = "whsec";
    private const string OrderBody = """{"event_type":"order.updated","payload":{"id":42,"event_id":7}}""";

    private readonly ITicketCheckoutEvents _events = Substitute.For<ITicketCheckoutEvents>();

    [HumansFact]
    public async Task SignedOrderEvent_NudgesTicketsWithTheBackendOrderId()
    {
        var result = await Controller(OrderBody, Sign(OrderBody)).Receive(TestContext.Current.CancellationToken);

        result.Should().BeOfType<OkResult>();
        await _events.Received(1).CheckoutChangedAsync("hievents", "7:42", Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SignedNonOrderEvent_IsAcknowledgedAndIgnored()
    {
        const string body = """{"event_type":"attendee.updated","payload":{"id":5,"event_id":7}}""";

        var result = await Controller(body, Sign(body)).Receive(TestContext.Current.CancellationToken);

        result.Should().BeOfType<OkResult>();
        await _events.DidNotReceiveWithAnyArgs().CheckoutChangedAsync(default!, default!, default);
    }

    [HumansFact]
    public async Task BadSignature_IsUnauthorizedAndNudgesNothing()
    {
        var result = await Controller(OrderBody, Sign("other body")).Receive(TestContext.Current.CancellationToken);

        result.Should().BeOfType<UnauthorizedResult>();
        await _events.DidNotReceiveWithAnyArgs().CheckoutChangedAsync(default!, default!, default);
    }

    [HumansFact]
    public async Task UnsetSecret_Is503AndNudgesNothing()
    {
        var result = await Controller(OrderBody, Sign(OrderBody), secret: "").Receive(TestContext.Current.CancellationToken);

        result.Should().BeOfType<StatusCodeResult>().Which.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        await _events.DidNotReceiveWithAnyArgs().CheckoutChangedAsync(default!, default!, default);
    }

    private HiEventsWebhookController Controller(string body, string signature, string secret = Secret)
    {
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Request.Headers["Signature"] = signature;

        return new HiEventsWebhookController(
            Options.Create(new HiEventsSettings { WebhookSecret = secret }),
            _events,
            NullLogger<HiEventsWebhookController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = context },
        };
    }

    private static string Sign(string body) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), Encoding.UTF8.GetBytes(body)));
}
