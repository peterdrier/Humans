using AwesomeAssertions;
using Humans.Base.Enums;
using Humans.Settings.Contracts;
using Humans.Tickets.Domain.Checkout;
using Humans.Tickets.Services.Checkout;
using Humans.Tickets.ViewComponents;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Humans.Tickets.Tests.ViewComponents;

/// <summary>No viewer check of its own: a Public viewer must see nothing.</summary>
public sealed class IssuedTicketsViewComponentTests
{
    private readonly ITicketCheckoutService _checkout = Substitute.For<ITicketCheckoutService>();
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();

    private IssuedTicketsViewComponent BuildSut()
    {
        var services = new ServiceCollection().AddSingleton(_checkout)
            .AddSingleton(Substitute.For<Microsoft.AspNetCore.Mvc.ViewEngines.ICompositeViewEngine>()).BuildServiceProvider();
        return new IssuedTicketsViewComponent(_settings)
        {
            ViewComponentContext = new ViewComponentContext
            {
                ViewContext = new ViewContext { HttpContext = new DefaultHttpContext { RequestServices = services } },
            },
        };
    }

    [HumansFact]
    public async Task RendersNothing_ForAPublicViewer()
    {
        var result = await BuildSut().InvokeAsync(Guid.NewGuid(), ProfileCardViewMode.Public);

        result.Should().BeOfType<ContentViewComponentResult>().Which.Content.Should().BeEmpty();
        await _checkout.DidNotReceiveWithAnyArgs().GetTicketsForHolderAsync(default, default);
    }

    [HumansFact]
    public async Task RendersNothing_WithoutTickets()
    {
        var userId = Guid.NewGuid();
        _checkout.GetTicketsForHolderAsync(userId, Arg.Any<CancellationToken>()).Returns([]);

        var result = await BuildSut().InvokeAsync(userId, ProfileCardViewMode.Self);

        result.Should().BeOfType<ContentViewComponentResult>();
    }

    [HumansFact]
    public async Task ListsTheHoldersTickets_NewestFirst_WithTheirEvent()
    {
        var userId = Guid.NewGuid();
        var salesEvent = new TicketSalesEvent { Id = Guid.NewGuid(), EventSettingsId = Guid.NewGuid() };
        _checkout.GetSalesEventsAsync(Arg.Any<CancellationToken>()).Returns([salesEvent]);
        _settings.GetAllEventSettingsAsync(Arg.Any<CancellationToken>()).Returns([
            new EventSettingsInfo(salesEvent.EventSettingsId, "Nowhere 2027", 2027, "Europe/Madrid",
                new NodaTime.LocalDate(2027, 6, 1), 0, 5, 7, new Dictionary<int, int>(), null, null)]);
        _checkout.GetTicketsForHolderAsync(userId, Arg.Any<CancellationToken>()).Returns([
            new TicketCheckoutTicket { SalesEventId = salesEvent.Id, Barcode = "HOLD", State = TicketCheckoutTicketState.Void, IssuedAt = NodaTime.Instant.FromUtc(2026, 1, 1, 0, 0) },
            new TicketCheckoutTicket { SalesEventId = salesEvent.Id, Barcode = "HNEW", IssuedAt = NodaTime.Instant.FromUtc(2027, 1, 1, 0, 0) },
        ]);

        var result = await BuildSut().InvokeAsync(userId, ProfileCardViewMode.Self);

        var model = (IssuedTicketsViewModel)result.Should().BeOfType<ViewViewComponentResult>().Which.ViewData!.Model!;
        model.Tickets.Select(t => (t.Barcode, t.EventName, t.State)).Should().Equal(
            ("HNEW", "Nowhere 2027", "Valid"), ("HOLD", "Nowhere 2027", "Void"));
    }

    [HumansFact]
    public async Task ListsAChildTicket_UnderTheAdultItRidesOn()
    {
        var userId = Guid.NewGuid();
        var adult = new TicketCheckoutTicket { Id = Guid.NewGuid(), OrderId = Guid.NewGuid(), HolderUserId = userId, Barcode = "HADULT" };
        var child = new TicketCheckoutTicket
        {
            Id = Guid.NewGuid(), OrderId = adult.OrderId, ParentTicketId = adult.Id, HolderName = "Kid",
            Kind = TicketHolderKind.Child, Barcode = "HCHILD",
        };
        var stranger = new TicketCheckoutTicket { Id = Guid.NewGuid(), OrderId = adult.OrderId, HolderUserId = Guid.NewGuid(), Barcode = "HOTHER" };
        _checkout.GetTicketsForHolderAsync(userId, Arg.Any<CancellationToken>()).Returns([adult]);
        _checkout.GetTicketsForOrderAsync(adult.OrderId, Arg.Any<CancellationToken>()).Returns([adult, child, stranger]);

        var result = await BuildSut().InvokeAsync(userId, ProfileCardViewMode.Self);

        var model = (IssuedTicketsViewModel)result.Should().BeOfType<ViewViewComponentResult>().Which.ViewData!.Model!;
        model.Tickets.Select(t => (t.Barcode, t.ChildName)).Should().Equal(("HADULT", null), ("HCHILD", "Kid"));
    }
}
