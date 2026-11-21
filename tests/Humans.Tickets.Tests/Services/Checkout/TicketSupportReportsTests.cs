using AwesomeAssertions;
using Humans.Tickets.Contracts;
using Humans.Tickets.Domain.Checkout;
using Humans.Tickets.Services.Checkout;
using NodaTime;
using Xunit;

namespace Humans.Tickets.Tests.Services.Checkout;

public sealed class TicketSupportReportsTests
{
    private static readonly DateTimeZone Madrid = DateTimeZoneProviders.Tzdb["Europe/Madrid"];
    private static readonly Instant Now = Instant.FromUtc(2027, 2, 1, 12, 0);

    private static TicketCheckoutOrder Order(
        TicketCheckoutOrderState state = TicketCheckoutOrderState.Paid,
        Instant? paidAt = null,
        params TicketCheckoutOrderLine[] lines) => new()
        {
            Id = Guid.NewGuid(),
            SalesEventId = Guid.NewGuid(),
            BuyerName = "Ana García",
            BuyerEmail = "ana@example.com",
            BackendOrderId = "cs_ABC123",
            State = state,
            PaidAt = state == TicketCheckoutOrderState.Paid ? paidAt ?? Instant.FromUtc(2027, 1, 10, 12, 0) : null,
            TotalMinor = lines.Sum(l => l.AmountMinor),
            Lines = lines.ToList(),
        };

    private static TicketCheckoutOrderLine Line(long amount = 22500) => new()
    {
        Kind = CheckoutLineKind.Ticket,
        Description = "Ticket: Wave 1",
        AmountMinor = amount,
        AttendeeName = "Ana García",
    };

    private static TicketCheckoutOrderLine Donation(long amount, string? purpose = null) => new()
    {
        Kind = CheckoutLineKind.Donation,
        Description = "Donation",
        AmountMinor = amount,
        DonationPurpose = purpose,
    };

    private static TicketCheckoutTicket Ticket(
        TicketCheckoutOrder order, long paid = 22500, string? classLabel = null,
        TicketCheckoutTicketState state = TicketCheckoutTicketState.Valid, TicketHolderKind kind = TicketHolderKind.Adult,
        string waveName = "Wave 1") => new()
        {
            Id = Guid.NewGuid(),
            OrderId = order.Id,
            SalesEventId = order.SalesEventId,
            Kind = kind,
            HolderName = "Bo Smith",
            HolderEmail = "bo@example.com",
            Barcode = "HX7Q2K",
            PricePaidMinor = paid,
            WaveName = waveName,
            ClassLabel = classLabel,
            State = state,
            IssuedAt = Now,
        };

    private static TicketCheckoutRefund Refund(Guid orderId, long amount, RefundState state, int minute = 0) => new()
    {
        Id = Guid.NewGuid(),
        OrderId = orderId,
        AmountMinor = amount,
        State = state,
        CreatedAt = Instant.FromUtc(2027, 2, 1, 10, minute),
    };

    private static TicketResaleOffer Offer(TicketCheckoutTicket ticket, int day, TicketResaleOfferState state = TicketResaleOfferState.Open) => new()
    {
        Id = Guid.NewGuid(),
        TicketId = ticket.Id,
        SalesEventId = ticket.SalesEventId,
        OfferedAt = Instant.FromUtc(2027, 1, day, 9, 0),
        State = state,
        RefundOwedMinor = ticket.PricePaidMinor,
    };

    [HumansTheory]
    [InlineData("garc")]
    [InlineData("Garcia")]
    [InlineData("ANA@EXAMPLE")]
    [InlineData("bo smi")]
    [InlineData("hx7q2k")]
    [InlineData(" cs_abc123 ")]
    public void Lookup_matches_names_emails_ticket_codes_and_backend_references(string query)
    {
        var order = Order(lines: Line());
        TicketSupportReports.Matches(order, [Ticket(order)], query).Should().BeTrue();
    }

    [HumansFact]
    public void Lookup_matches_an_order_id_or_reference_only_exactly()
    {
        var order = Order(lines: Line());

        TicketSupportReports.Matches(order, [], order.Id.ToString()).Should().BeTrue();
        TicketSupportReports.Matches(order, [], order.Reference.ToLowerInvariant()).Should().BeTrue();
        TicketSupportReports.Matches(order, [], order.Reference[..6]).Should().BeFalse();
        TicketSupportReports.Matches(order, [], Guid.NewGuid().ToString()).Should().BeFalse();
    }

    [HumansTheory]
    [InlineData("an")]
    [InlineData("HX7Q")]
    [InlineData("cs_ABC")]
    public void Lookup_ignores_short_queries_and_partial_codes(string query)
    {
        var order = Order(lines: Line());
        TicketSupportReports.Matches(order, [Ticket(order)], query).Should().BeFalse();
    }

    [HumansFact]
    public void Refund_list_filters_by_state_newest_first_and_drops_refunds_of_other_events()
    {
        var order = Order(lines: Line());
        var failed = Refund(order.Id, 22500, RefundState.Failed, minute: 1);
        var manual = Refund(order.Id, 22500, RefundState.ManualRequired, minute: 2);
        var elsewhere = Refund(Guid.NewGuid(), 100, RefundState.Failed);

        TicketSupportReports.RefundList([order], [failed, manual, elsewhere], null)
            .Select(r => r.Refund).Should().Equal(manual, failed);
        TicketSupportReports.RefundList([order], [failed, manual, elsewhere], RefundState.Failed)
            .Should().ContainSingle().Which.Order.Should().BeSameAs(order);
    }

    [HumansFact]
    public void Resale_queue_lists_waiting_offers_oldest_first_and_an_invited_offer_keeps_its_place()
    {
        var order = Order(lines: Line());
        var (first, second, invited, sold) = (Ticket(order), Ticket(order), Ticket(order), Ticket(order));
        var newer = Offer(second, day: 5);
        var older = Offer(first, day: 2);
        var directed = Offer(invited, day: 1);
        directed.Invites = [new TicketResaleInvite { Email = "friend@example.com", TokenHash = "hash", SentAt = directed.OfferedAt }];
        var done = Offer(sold, day: 1, TicketResaleOfferState.Sold);
        var refund = Refund(order.Id, 22500, RefundState.Failed);
        done.RefundId = refund.Id;

        var rows = TicketSupportReports.ResaleQueue([newer, done, older, directed], [first, second, invited, sold], [refund]);

        rows.Select(r => (r.Offer, r.Position, r.DirectedLive)).Should().Equal(
            (directed, (int?)1, true), (older, 2, false), (newer, 3, false), (done, null, false));
        rows[1].Ticket.Should().BeSameAs(first);
        rows[3].Refund.Should().BeSameAs(refund);
    }

    [HumansFact]
    public void Sales_report_groups_issued_tickets_by_class_or_wave_and_splits_donations()
    {
        var wave = Order(lines: [Line(), Donation(17500, "Low income pool")]);
        var lowIncome = Order(lines: Line(12500));
        lowIncome.DisputedMinor = 12500;
        var unpaid = Order(TicketCheckoutOrderState.Pending, lines: Line());
        var tickets = new[]
        {
            Ticket(wave, state: TicketCheckoutTicketState.Resold),
            Ticket(lowIncome, 12500, classLabel: "Low income"),
            Ticket(lowIncome, 0, kind: TicketHolderKind.Companion),
            Ticket(wave, 0, kind: TicketHolderKind.Child, waveName: "Under 6"),
            Ticket(wave, 5000, kind: TicketHolderKind.Child, waveName: "Under 13"),
            Ticket(unpaid),
        };

        var report = TicketSupportReports.Sales([wave, lowIncome, unpaid], tickets, [], [], Madrid);

        report.TicketsIssued.Should().Be(5);
        report.TicketsLive.Should().Be(4);
        report.TicketRevenueMinor.Should().Be(35000);
        report.DonationsMinor.Should().Be(17500);
        report.DisputedMinor.Should().Be(12500);
        report.ByPrice.Should().BeEquivalentTo(
            [new SalesRow("Child: Under 13", 1, 5000), new SalesRow("Child: Under 6", 1, 0), new SalesRow("Companion", 1, 0),
             new SalesRow("Low income", 1, 12500), new SalesRow("Wave 1", 1, 22500)]);
        report.DonationsByPurpose.Should().Equal(new DonationRow("Low income pool", 1, 17500));
    }

    [HumansFact]
    public void Sales_by_day_uses_the_event_time_zone()
    {
        // 23:30 UTC on 10 January is already 11 January in Madrid.
        var late = Order(paidAt: Instant.FromUtc(2027, 1, 10, 23, 30), lines: [Line(), Donation(1000)]);
        var early = Order(paidAt: Instant.FromUtc(2027, 1, 10, 9, 0), lines: Line());

        TicketSupportReports.Sales([late, early], [], [], [], Madrid).ByDay.Should().Equal(
            new DayRow(new LocalDate(2027, 1, 10), 1, 22500, 0),
            new DayRow(new LocalDate(2027, 1, 11), 1, 22500, 1000));
    }

    [HumansFact]
    public void Sales_report_totals_refunds_and_resale_offers_by_state()
    {
        var order = Order(lines: Line());
        var ticket = Ticket(order);
        var report = TicketSupportReports.Sales([order], [ticket],
            [Refund(order.Id, 22500, RefundState.Failed), Refund(order.Id, 22500, RefundState.Succeeded)],
            [Offer(ticket, 1, TicketResaleOfferState.Sold), Offer(ticket, 2)], Madrid);

        report.RefundsByState.Should().Equal(
            new RefundStateRow(RefundState.Succeeded, 1, 22500),
            new RefundStateRow(RefundState.Failed, 1, 22500));
        report.ResaleByState.Should().Equal(
            new ResaleStateRow(TicketResaleOfferState.Open, 1, 22500),
            new ResaleStateRow(TicketResaleOfferState.Sold, 1, 22500));
    }
}
