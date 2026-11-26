using Humans.Testing;
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
            users, Substitute.For<IStringLocalizer<TicketsResource>>().AddFormatting(), NullLogger<TicketBuyController>.Instance)
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
        new(new TicketingSettings { Id = Guid.NewGuid() }, "Event", null, null, null, false, "Buyer", orders)
        { GateOpeningDate = gates };

    [HumansFact]
    public async Task Mine_OrdersFamiliesWithinTheEventAndKeepsDependentsBesideTheirHolder()
    {
        var eventId = Guid.NewGuid();
        TicketCheckoutTicket Ticket(Instant issued, Guid? holder, Guid? parent = null) => new()
        { Id = Guid.NewGuid(), TicketingSettingsId = eventId, HolderUserId = holder, ParentTicketId = parent, IssuedAt = issued };
        HeldTicket Held(TicketCheckoutTicket ticket) => new(ticket, "Event", null, null, false, false, false);
        var later = Held(Ticket(Now + Duration.FromDays(1), _userId));
        var laterChild = Held(Ticket(Now, null, later.Ticket.Id));
        var earlier = Held(Ticket(Now, _userId));
        var earlierCompanion = Held(Ticket(Now + Duration.FromDays(2), Guid.NewGuid(), earlier.Ticket.Id));
        var ownCompanion = Held(Ticket(Now - Duration.FromDays(1), _userId, Guid.NewGuid()));
        var otherEvent = Held(new TicketCheckoutTicket
        { Id = Guid.NewGuid(), TicketingSettingsId = Guid.NewGuid(), HolderUserId = _userId, IssuedAt = Now - Duration.FromDays(2) });
        _checkout.GetHeldTicketsAsync(_userId, Arg.Any<CancellationToken>())
            .Returns([later, laterChild, earlier, earlierCompanion, ownCompanion, otherEvent]);

        var result = (ViewResult)await Controller().Mine(TestContext.Current.CancellationToken);

        ((IReadOnlyList<HeldTicket>)result.Model!).Should().Equal(ownCompanion, earlier, earlierCompanion, later, laterChild, otherEvent);
    }

    [HumansFact]
    public async Task Order_PresentsTicketsByKindThenIssueTime()
    {
        var order = new TicketCheckoutOrder { Id = Guid.NewGuid(), BuyerUserId = _userId };
        var older = new TicketCheckoutTicket { Id = Guid.NewGuid(), Kind = TicketHolderKind.Adult, IssuedAt = Now };
        var newer = new TicketCheckoutTicket { Id = Guid.NewGuid(), Kind = TicketHolderKind.Adult, IssuedAt = Now + Duration.FromDays(1) };
        var child = new TicketCheckoutTicket { Id = Guid.NewGuid(), Kind = TicketHolderKind.Child, IssuedAt = Now - Duration.FromDays(1) };
        _checkout.GetBuyOrderAsync(order.Id, _userId, Arg.Any<CancellationToken>())
            .Returns(new BuyOrder(order.Id, order.Reference, order.State, order.IsOpen, true, 0, 0, 0, null, [], [child, newer, older]));

        var result = (ViewResult)await Controller().Order(order.Id, TestContext.Current.CancellationToken);

        ((BuyOrder)result.Model!).Tickets.Should().Equal(older, newer, child);
    }

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

        pages.Select(page => page.Ticketing.Id).Should().Equal(earlierEvent.Ticketing.Id, laterEvent.Ticketing.Id);
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

    private static readonly Guid ViewerId = Guid.NewGuid();

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
            Substitute.For<IStringLocalizer<TicketsResource>>().AddFormatting(), NullLogger<TicketBuyController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
            TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>()),
        };
    }

    [HumansFact]
    public async Task Order_RestrictedReadReturnsNull_NotFound()
    {
        var order = new TicketCheckoutOrder { Id = Guid.NewGuid(), BuyerUserId = Guid.NewGuid() };

        var result = await NewController().Order(order.Id, Xunit.TestContext.Current.CancellationToken);

        result.Should().BeOfType<NotFoundResult>();
        await _checkout.DidNotReceiveWithAnyArgs().RefreshOrderAsync(default);
    }

    [HumansFact]
    public async Task Order_AuthorizedRead_ReturnsView()
    {
        var order = new TicketCheckoutOrder { Id = Guid.NewGuid(), BuyerUserId = ViewerId };
        _checkout.GetBuyOrderAsync(order.Id, ViewerId, Arg.Any<CancellationToken>())
            .Returns(new BuyOrder(order.Id, order.Reference, order.State, order.IsOpen, true, 0, 0, 0, null, [], []));

        var result = await NewController().Order(order.Id, Xunit.TestContext.Current.CancellationToken);

        result.Should().BeOfType<ViewResult>();
        await _checkout.Received(1).GetBuyOrderAsync(order.Id, ViewerId, Arg.Any<CancellationToken>());
    }
}
