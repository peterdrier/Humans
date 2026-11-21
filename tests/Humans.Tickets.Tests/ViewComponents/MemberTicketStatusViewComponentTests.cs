using System.Security.Claims;
using AwesomeAssertions;
using Humans.Settings.Contracts;
using Humans.Tickets.Contracts;
using Humans.Tickets.Domain.Checkout;
using Humans.Tickets.Services.Checkout;
using Humans.Tickets.ViewComponents;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewEngines;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NSubstitute;
using Xunit;

namespace Humans.Tickets.Tests.ViewComponents;

/// <summary>The member dashboard's ticket card renders nothing off season, and an unavailable state when holdings fail to load.</summary>
public sealed class MemberTicketStatusViewComponentTests
{
    private readonly ITicketService _tickets = Substitute.For<ITicketService>();
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();

    private static readonly Guid EventId = Guid.NewGuid();

    private static EventSettingsInfo Event(bool isOffSeason) => new(
        EventId, "Test Event", 2026, "UTC", new LocalDate(2026, 7, 1),
        -14, 6, 9, new Dictionary<int, int>(), null, null,
        IsOffSeason: isOffSeason);

    private MemberTicketStatusViewComponent BuildSut(
        TicketVendorSettings? vendor = null, TicketSalesEvent? sale = null, TicketCheckoutTicket? held = null)
    {
        var checkout = Substitute.For<ITicketCheckoutService>();
        checkout.GetSalesEventsAsync(Arg.Any<CancellationToken>()).Returns(sale is null ? [] : [sale]);
        checkout.GetTicketsForHolderAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(held is null ? [] : [held]);
        var httpContext = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddSingleton(checkout)
                .AddSingleton(Substitute.For<ICompositeViewEngine>())
                .BuildServiceProvider(),
        };
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], "test"));
        return new MemberTicketStatusViewComponent(
            _tickets, _settings, Options.Create(vendor ?? new TicketVendorSettings()),
            NullLogger<MemberTicketStatusViewComponent>.Instance)
        {
            ViewComponentContext = new ViewComponentContext
            {
                ViewContext = new ViewContext { HttpContext = httpContext },
            },
        };
    }

    [HumansFact]
    public async Task InvokeAsync_OffSeason_RendersNothing()
    {
        _settings.GetActiveEventSettings().Returns(Event(isOffSeason: true));

        var result = await BuildSut().InvokeAsync();

        result.Should().BeOfType<ContentViewComponentResult>().Which.Content.Should().BeEmpty();
    }

    [HumansFact]
    public async Task InvokeAsync_InSeason_RendersTheCardWithTheEventYear()
    {
        _settings.GetActiveEventSettings().Returns(Event(isOffSeason: false));

        var result = await BuildSut().InvokeAsync();

        var model = (MemberTicketStatusViewModel)((ViewViewComponentResult)result).ViewData!.Model!;
        model.EventYear.Should().Be(2026);
    }

    [HumansTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InvokeAsync_HoldingsReadFails_ShowsUnavailableNotMissing(bool fails)
    {
        _settings.GetActiveEventSettings().Returns(Event(isOffSeason: false));
        if (fails)
            _tickets.GetUserTicketHoldingsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                .Returns<UserTicketHoldings>(_ => throw new InvalidOperationException("vendor down"));
        else
            _tickets.GetUserTicketHoldingsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                .Returns(new UserTicketHoldings(0, []));

        var result = await BuildSut(new TicketVendorSettings { EventId = "ev", ApiKey = "key" }).InvokeAsync();

        var model = (MemberTicketStatusViewModel)((ViewViewComponentResult)result).ViewData!.Model!;
        model.HoldingsUnavailable.Should().Be(fails);
        model.HasTicket.Should().BeFalse();
    }

    private static TicketSalesEvent Sale(TicketSalesMode mode) =>
        new() { Id = Guid.NewGuid(), EventSettingsId = EventId, BackendKey = "boxoffice", Mode = mode };

    [HumansFact]
    public async Task InvokeAsync_OffSeasonWithASaleOpen_StillOffersTheBuyPage()
    {
        _settings.GetActiveEventSettings().Returns(Event(isOffSeason: true));

        var result = await BuildSut(sale: Sale(TicketSalesMode.Open)).InvokeAsync();

        var model = (MemberTicketStatusViewModel)((ViewViewComponentResult)result).ViewData!.Model!;
        model.SellsInHumans.Should().BeTrue();
    }

    [HumansFact]
    public async Task InvokeAsync_HumansTicketHolderWithNoVendorSync_HasTicket()
    {
        _settings.GetActiveEventSettings().Returns(Event(isOffSeason: false));
        var sale = Sale(TicketSalesMode.Closed);
        var held = new TicketCheckoutTicket { Id = Guid.NewGuid(), SalesEventId = sale.Id, State = TicketCheckoutTicketState.Valid };

        var result = await BuildSut(sale: sale, held: held).InvokeAsync();

        var model = (MemberTicketStatusViewModel)((ViewViewComponentResult)result).ViewData!.Model!;
        model.TicketsConfigured.Should().BeFalse();
        model.HumansTicketCount.Should().Be(1);
        model.HasTicket.Should().BeTrue();
    }
}
