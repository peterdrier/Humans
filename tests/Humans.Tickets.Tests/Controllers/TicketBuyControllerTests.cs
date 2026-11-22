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
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;
using Xunit;

namespace Humans.Tickets.Tests.Controllers;

public class TicketBuyControllerTests
{
    private readonly ITicketCheckoutService _checkout = Substitute.For<ITicketCheckoutService>();
    private readonly Guid _userId = Guid.NewGuid();
    private static readonly Instant Now = Instant.FromUtc(2027, 1, 1, 12, 0);

    private TicketBuyController Controller(bool authenticated = true)
    {
        var users = Substitute.For<IUserService>();
        users.GetUserInfoAsync(_userId, Arg.Any<CancellationToken>()).Returns(
            new UserInfo(_userId, "Buyer", false, "en", null, Now, Now, null, null, null, null, null, null, null, null, [], [], null));
        return new TicketBuyController(_checkout, Substitute.For<IMagicLinkService>(), Substitute.For<IEarlyEntryService>(),
            users, Substitute.For<IStringLocalizer<TicketsResource>>(), NullLogger<TicketBuyController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = authenticated
                        ? new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, _userId.ToString())], "Test"))
                        : new ClaimsPrincipal(),
                },
            },
        };
    }

    private static BuyPage Page(LocalDate gates, params TicketCheckoutOrder[] orders) =>
        new(new TicketSalesEvent { Id = Guid.NewGuid() }, "Event", null, null, null, false, "Buyer", orders)
        { GateOpeningDate = gates };

    [HumansFact]
    public async Task Index_PresentsEventsByGateDateAndOrderHistoryNewestFirst()
    {
        var older = new TicketCheckoutOrder { Id = Guid.NewGuid(), CreatedAt = Now };
        var newer = new TicketCheckoutOrder { Id = Guid.NewGuid(), CreatedAt = Now + Duration.FromDays(1) };
        var laterEvent = Page(new LocalDate(2027, 8, 1), older, newer);
        var earlierEvent = Page(new LocalDate(2027, 7, 1));
        _checkout.GetBuyPagesAsync(_userId, Arg.Any<CancellationToken>()).Returns([laterEvent, earlierEvent]);

        var result = (ViewResult)await Controller().Index(TestContext.Current.CancellationToken);
        var pages = (IReadOnlyList<BuyPage>)result.Model!;

        pages.Select(page => page.SalesEvent.Id).Should().Equal(earlierEvent.SalesEvent.Id, laterEvent.SalesEvent.Id);
        pages[1].Orders.Should().Equal(newer, older);
        laterEvent.Orders.Should().Equal(older, newer);
    }

    [HumansFact]
    public async Task Public_PresentsEventsByGateDate()
    {
        var later = new PublicSale("Later", 100, false) { GateOpeningDate = new LocalDate(2027, 8, 1) };
        var earlier = new PublicSale("Earlier", 200, true) { GateOpeningDate = new LocalDate(2027, 7, 1) };
        _checkout.GetPublicSalesAsync(Arg.Any<CancellationToken>()).Returns([later, earlier]);

        var result = (ViewResult)await Controller(authenticated: false).Public(TestContext.Current.CancellationToken);

        ((IReadOnlyList<PublicSale>)result.Model!).Should().Equal(earlier, later);
    }

    [HumansFact]
    public async Task DirectedResale_PresentsOrderHistoryNewestFirstAndRetainsTheToken()
    {
        var older = new TicketCheckoutOrder { Id = Guid.NewGuid(), CreatedAt = Now };
        var newer = new TicketCheckoutOrder { Id = Guid.NewGuid(), CreatedAt = Now + Duration.FromDays(1) };
        _checkout.GetDirectedBuyPageAsync("invite", _userId, Arg.Any<CancellationToken>())
            .Returns(Page(new LocalDate(2027, 7, 1), older, newer));
        var controller = Controller();

        var result = (ViewResult)await controller.Resale("invite", TestContext.Current.CancellationToken);

        ((IReadOnlyList<BuyPage>)result.Model!).Should().ContainSingle().Which.Orders.Should().Equal(newer, older);
        controller.ViewData["DirectedToken"].Should().Be("invite");
    }
}
