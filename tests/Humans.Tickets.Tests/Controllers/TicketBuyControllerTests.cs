using System.Security.Claims;
using AwesomeAssertions;
using Humans.Auth.Contracts;
using Humans.EarlyEntry.Contracts;
using Humans.Tickets.Controllers;
using Humans.Tickets.Domain.Checkout;
using Humans.Tickets.Services.Checkout;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;

namespace Humans.Tickets.Tests.Controllers;

public class TicketBuyControllerTests
{
    private static readonly Guid ViewerId = Guid.NewGuid();
    private readonly ITicketCheckoutService _checkout = Substitute.For<ITicketCheckoutService>();

    private TicketBuyController NewController()
    {
        var users = Substitute.For<IUserService>();
        users.GetUserInfoAsync(ViewerId, Arg.Any<CancellationToken>()).Returns(new ValueTask<UserInfo?>(
            new UserInfo(ViewerId, "Viewer", false, "en", null, Instant.MinValue, null, null, null, null, null, null, null, null,
                "viewer@example.com", [], [], null)));
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, ViewerId.ToString())], "test")),
        };
        return new TicketBuyController(_checkout, Substitute.For<IMagicLinkService>(), Substitute.For<IEarlyEntryService>(), users,
            Substitute.For<IStringLocalizer<TicketsResource>>(), NullLogger<TicketBuyController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
            TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>()),
        };
    }

    private void Stored(TicketCheckoutOrder order) =>
        _checkout.GetOrderDetailAsync(order.Id, Arg.Any<CancellationToken>()).Returns(new CheckoutOrderDetail(order, [], [], []));

    [HumansFact]
    public async Task Someone_elses_order_is_not_found_and_never_refreshed_from_the_backend()
    {
        var order = new TicketCheckoutOrder { Id = Guid.NewGuid(), BuyerUserId = Guid.NewGuid() };
        Stored(order);

        var result = await NewController().Order(order.Id, Xunit.TestContext.Current.CancellationToken);

        result.Should().BeOfType<NotFoundResult>();
        await _checkout.DidNotReceiveWithAnyArgs().RefreshOrderAsync(default, default);
    }

    [HumansFact]
    public async Task The_buyer_sees_their_order_refreshed_from_the_backend()
    {
        var order = new TicketCheckoutOrder { Id = Guid.NewGuid(), BuyerUserId = ViewerId };
        Stored(order);
        _checkout.RefreshOrderAsync(order.Id, Arg.Any<CancellationToken>()).Returns(order);

        var result = await NewController().Order(order.Id, Xunit.TestContext.Current.CancellationToken);

        result.Should().BeOfType<ViewResult>();
        await _checkout.Received(1).RefreshOrderAsync(order.Id, Arg.Any<CancellationToken>());
    }
}
