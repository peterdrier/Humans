using AwesomeAssertions;
using Humans.Integration.Tests.Infrastructure;
using Humans.Tickets.Contracts;
using Humans.Tickets.Data;
using Humans.Tickets.Domain.Checkout;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Xunit;

namespace Humans.Integration.Tests.Repositories.Tickets;

/// <summary>
/// The JSON-owned lists (order lines, waves, price rules) must survive an update of a detached copy, which is how the
/// service hands them back. The in-memory fakes cannot catch EF's shadow-key failure here.
/// </summary>
public class TicketCheckoutRepositoryTests(HumansTestDatabase database) : IntegrationTestBase(database)
{
    private static readonly Instant Now = Instant.FromUtc(2027, 1, 10, 12, 0);

    [HumansFact]
    public async Task Detached_order_and_sales_event_round_trip_their_json_lists()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = Factory.Services.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<ITicketCheckoutRepository>();
        var holder = Guid.NewGuid();

        var salesEvent = new TicketSalesEvent
        {
            Id = Guid.NewGuid(),
            EventSettingsId = Guid.NewGuid(),
            BackendKey = "boxoffice",
            Capacity = 10,
            Waves = [new TicketSalesWave { Name = "Wave 1", EndsOn = new LocalDate(2027, 1, 31), PriceMinor = 22500 }],
            SupporterTotalsMinor = [40000],
            CreatedAt = Now,
            UpdatedAt = Now,
        };
        await repo.SaveSalesEventAsync(salesEvent, ct);
        var loadedEvent = (await repo.GetSalesEventAsync(salesEvent.Id, ct))!;
        loadedEvent.Waves.Add(new TicketSalesWave { Name = "Wave 2", EndsOn = new LocalDate(2027, 2, 28), PriceMinor = 25000 });
        loadedEvent.PriceRules.Add(new TicketPriceRule { GrantProgramId = Guid.NewGuid(), Kind = TicketPriceRuleKind.Credit, AmountMinor = 5000, Label = "Credit" });
        await repo.SaveSalesEventAsync(loadedEvent, ct);

        var order = new TicketCheckoutOrder
        {
            Id = Guid.NewGuid(),
            SalesEventId = salesEvent.Id,
            BuyerUserId = holder,
            BuyerName = "Ana",
            BuyerEmail = "ana@example.com",
            BackendKey = "boxoffice",
            TotalMinor = 22500,
            CreatedAt = Now,
            ExpiresAt = Now + Duration.FromHours(1),
            UpdatedAt = Now,
            Lines = [new TicketCheckoutOrderLine { Kind = CheckoutLineKind.Ticket, Description = "Ticket", AmountMinor = 22500, AttendeeUserId = holder }],
        };
        await repo.AddOrderAsync(order, ct);
        var loaded = (await repo.GetOrderAsync(order.Id, ct))!;
        loaded.BackendOrderId = "cs_1";
        loaded.State = TicketCheckoutOrderState.Paid;
        loaded.Lines[0].WaveName = "Wave 1";
        await repo.UpdateOrderAsync(loaded, ct);
        await repo.UpdateOrderAsync(loaded, ct);

        await repo.ErasePiiForUserAsync(holder, ct);
        await repo.ReassignAsync(holder, Guid.NewGuid(), ct);

        var stored = (await repo.GetOrderAsync(order.Id, ct))!;
        stored.BackendOrderId.Should().Be("cs_1");
        stored.State.Should().Be(TicketCheckoutOrderState.Paid);
        stored.Lines.Should().ContainSingle().Which.WaveName.Should().Be("Wave 1");
        stored.Lines[0].AttendeeUserId.Should().NotBe(holder);
        stored.BuyerName.Should().NotBe("Ana");
        var storedEvent = (await repo.GetSalesEventAsync(salesEvent.Id, ct))!;
        storedEvent.Waves.Select(w => w.Name).Should().Equal("Wave 1", "Wave 2");
        storedEvent.PriceRules.Should().ContainSingle();
    }

    [HumansFact]
    public async Task Tickets_and_offers_round_trip_and_erasure_clears_their_personal_data()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = Factory.Services.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<ITicketCheckoutRepository>();
        var holder = Guid.NewGuid();
        var salesEventId = Guid.NewGuid();
        var adult = new TicketCheckoutTicket
        {
            Id = Guid.NewGuid(),
            SalesEventId = salesEventId,
            OrderId = Guid.NewGuid(),
            HolderUserId = holder,
            HolderName = "Ana García",
            HolderEmail = "ana@example.com",
            Barcode = "H" + Guid.NewGuid().ToString("N")[..16],
            PricePaidMinor = 22500,
            WaveName = "Wave 1",
            IssuedAt = Now,
        };
        var child = new TicketCheckoutTicket
        {
            Id = Guid.NewGuid(),
            SalesEventId = salesEventId,
            OrderId = adult.OrderId,
            Kind = TicketHolderKind.Child,
            ParentTicketId = adult.Id,
            HolderName = "Kid García",
            Barcode = "H" + Guid.NewGuid().ToString("N")[..16],
            WaveName = "Child",
            IssuedAt = Now,
        };
        await repo.AddTicketsAsync([adult, child], ct);
        var offer = new TicketResaleOffer
        {
            Id = Guid.NewGuid(),
            SalesEventId = salesEventId,
            TicketId = adult.Id,
            HolderUserId = holder,
            OfferedAt = Now,
            RefundOwedMinor = 22500,
            DirectedEmail = "bo@example.com",
            DirectedTokenHash = "ABC",
            DirectedExpiresAt = Now + Duration.FromDays(3),
        };
        await repo.AddOfferAsync(offer, ct);
        adult.State = TicketCheckoutTicketState.Offered;
        await repo.UpdateTicketAsync(adult, ct);

        (await repo.GetTicketByBarcodeAsync(adult.Barcode, ct))!.State.Should().Be(TicketCheckoutTicketState.Offered);
        (await repo.GetTicketsForOrderAsync(adult.OrderId, ct)).Should().HaveCount(2);
        (await repo.GetOfferByTokenHashAsync("ABC", ct))!.Id.Should().Be(offer.Id);

        await repo.ErasePiiForUserAsync(holder, ct);

        var stored = (await repo.GetTicketAsync(adult.Id, ct))!;
        stored.HolderName.Should().NotBe("Ana García");
        stored.HolderEmail.Should().NotBe("ana@example.com");
        stored.Barcode.Should().Be(adult.Barcode);
        var storedOffer = (await repo.GetOfferAsync(offer.Id, ct))!;
        storedOffer.DirectedEmail.Should().BeNull();
        storedOffer.DirectedTokenHash.Should().BeNull();
        storedOffer.RefundOwedMinor.Should().Be(22500);
    }
}
