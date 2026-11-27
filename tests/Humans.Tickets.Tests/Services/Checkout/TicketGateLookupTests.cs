using AwesomeAssertions;
using Humans.Testing;
using Humans.Tickets.Contracts;
using Humans.Tickets.Domain.Checkout;
using Humans.Tickets.Services.Checkout;
using Xunit;

namespace Humans.Tickets.Tests.Services.Checkout;

public sealed class TicketGateLookupTests : CheckoutServiceFixture
{
    private readonly TicketGateLookup _lookup;

    public TicketGateLookupTests()
    {
        _lookup = new TicketGateLookup(_repo, _clock);
    }

    private TicketCheckoutTicket AddTicket(
        TicketHolderKind kind = TicketHolderKind.Adult, Guid? holder = null, Guid? parent = null,
        TicketCheckoutTicketState state = TicketCheckoutTicketState.Valid, string? classLabel = null)
    {
        var ticket = new TicketCheckoutTicket
        {
            Id = Guid.NewGuid(),
            SalesEventId = _salesEvent.Id,
            OrderId = Guid.NewGuid(),
            Kind = kind,
            ParentTicketId = parent,
            HolderUserId = holder,
            HolderName = "Ana García",
            Barcode = TicketCheckoutService.NewBarcode(),
            WaveName = "Wave 1",
            ClassLabel = classLabel,
            State = state,
        };
        _repo.Tickets.Add(ticket);
        return ticket;
    }

    [HumansFact]
    public async Task Gate_finds_an_issued_ticket_by_its_code_with_its_event_and_holder()
    {
        var ticket = AddTicket(holder: _buyerId, classLabel: "Low income");

        var found = await _lookup.FindIssuedTicketAsync($" {ticket.Barcode} ", TestContext.Current.CancellationToken);

        found.Should().Be(new IssuedTicketInfo(ticket.Id, _event.Id, ticket.Barcode, "Ana García", _buyerId, _buyerId,
            "Low income", IsValid: true));
        (await _lookup.FindIssuedTicketAsync("HNOSUCHCODE", TestContext.Current.CancellationToken)).Should().BeNull();
        (await _lookup.FindIssuedTicketAsync(" ", TestContext.Current.CancellationToken)).Should().BeNull();
    }

    [HumansTheory]
    [InlineData((int)TicketCheckoutTicketState.Valid, false, true, false)]
    [InlineData((int)TicketCheckoutTicketState.Offered, false, false, true)]
    [InlineData((int)TicketCheckoutTicketState.Offered, true, true, false)]
    [InlineData((int)TicketCheckoutTicketState.Resold, false, false, false)]
    [InlineData((int)TicketCheckoutTicketState.Void, false, false, false)]
    public async Task Gate_refuses_a_ticket_that_can_still_sell_on_resale_and_resold_or_void_ones(
        int state, bool salesClosed, bool valid, bool inQueue)
    {
        var ticket = AddTicket(holder: _buyerId, state: (TicketCheckoutTicketState)state);
        if (salesClosed)
            _salesEvent.Mode = TicketSalesMode.Closed;

        var found = await _lookup.FindIssuedTicketAsync(ticket.Barcode, TestContext.Current.CancellationToken);

        found!.IsValid.Should().Be(valid);
        found.InResaleQueue.Should().Be(inQueue);
    }

    [HumansFact]
    public async Task Gate_refuses_a_child_ticket_while_its_adults_offer_can_still_sell()
    {
        var adult = AddTicket(holder: _buyerId, state: TicketCheckoutTicketState.Offered);
        var child = AddTicket(TicketHolderKind.Child, parent: adult.Id);

        var found = await _lookup.FindIssuedTicketAsync(child.Barcode, TestContext.Current.CancellationToken);
        found!.IsValid.Should().BeFalse();
        found.InResaleQueue.Should().BeTrue();

        _salesEvent.Mode = TicketSalesMode.Closed;
        (await _lookup.FindIssuedTicketAsync(child.Barcode, TestContext.Current.CancellationToken))!.IsValid.Should().BeTrue();
    }

    [HumansFact]
    public async Task Gate_takes_a_childs_early_entry_from_the_adult_it_belongs_to()
    {
        var adult = AddTicket(holder: _buyerId);
        var child = AddTicket(TicketHolderKind.Child, parent: adult.Id);

        var found = await _lookup.FindIssuedTicketAsync(child.Barcode, TestContext.Current.CancellationToken);

        found!.HolderUserId.Should().BeNull();
        found.EntryUserId.Should().Be(_buyerId);
        found.TicketLabel.Should().Be("Child · Wave 1");
    }

    [HumansFact]
    public async Task Gate_lists_every_issued_ticket_of_an_event_for_name_search()
    {
        var adult = AddTicket(holder: _buyerId);
        var child = AddTicket(TicketHolderKind.Child, parent: adult.Id);
        var gone = AddTicket(holder: _buyerId, state: TicketCheckoutTicketState.Void);

        var all = await _lookup.GetIssuedTicketsForEventAsync(_event.Id, Ct);

        all.Select(t => t.Id).Should().BeEquivalentTo([adult.Id, child.Id, gone.Id]);
        all.Single(t => t.Id == child.Id).EntryUserId.Should().Be(_buyerId);
        all.Single(t => t.Id == gone.Id).IsValid.Should().BeFalse();
        (await _lookup.GetIssuedTicketsForEventAsync(Guid.NewGuid(), Ct)).Should().BeEmpty();
    }
}
