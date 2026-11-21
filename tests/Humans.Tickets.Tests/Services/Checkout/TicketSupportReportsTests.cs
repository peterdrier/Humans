using System.Reflection;
using AwesomeAssertions;
using Humans.Tickets.Contracts;
using Humans.Tickets.Controllers;
using Humans.Tickets.Domain.Checkout;
using Humans.Tickets.Services.Checkout;
using Microsoft.AspNetCore.Authorization;
using NodaTime;
using Xunit;

namespace Humans.Tickets.Tests.Services.Checkout;

public sealed class TicketSupportReportsTests
{
    private static readonly DateTimeZone Madrid = DateTimeZoneProviders.Tzdb["Europe/Madrid"];

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

    private static TicketCheckoutOrderLine Ticket(string description = "Ticket: Wave 1", long amount = 22500,
        string holder = "Ana García", string? barcode = "HX-7Q2") => new()
        {
            Kind = CheckoutLineKind.Ticket,
            Description = description,
            AmountMinor = amount,
            AttendeeName = holder,
            AttendeeEmail = "holder@example.com",
            Barcode = barcode,
        };

    private static TicketCheckoutOrderLine Donation(long amount, TicketDonationPurpose purpose = TicketDonationPurpose.General) => new()
    {
        Kind = CheckoutLineKind.Donation,
        Description = "Donation",
        AmountMinor = amount,
        DonationPurpose = purpose,
    };

    private static TicketCheckoutRefund Refund(Guid orderId, long amount, RefundState state, int minute = 0) => new()
    {
        Id = Guid.NewGuid(),
        OrderId = orderId,
        AmountMinor = amount,
        State = state,
        CreatedAt = Instant.FromUtc(2027, 2, 1, 10, minute),
    };

    [HumansTheory]
    [InlineData("garc")]
    [InlineData("ANA@EXAMPLE")]
    [InlineData("holder@")]
    [InlineData("hx-7q2")]
    [InlineData(" cs_abc123 ")]
    public void Lookup_matches_names_emails_ticket_codes_and_backend_references(string query)
    {
        TicketSupportReports.Matches(Order(lines: Ticket()), query).Should().BeTrue();
    }

    [HumansFact]
    public void Lookup_matches_an_order_id_only_exactly()
    {
        var order = Order(lines: Ticket());

        TicketSupportReports.Matches(order, order.Id.ToString()).Should().BeTrue();
        TicketSupportReports.Matches(order, Guid.NewGuid().ToString()).Should().BeFalse();
    }

    [HumansTheory]
    [InlineData("an")]
    [InlineData("HX-7")]
    [InlineData("cs_ABC")]
    public void Lookup_ignores_short_queries_and_partial_codes(string query)
    {
        TicketSupportReports.Matches(Order(lines: Ticket()), query).Should().BeFalse();
    }

    [HumansFact]
    public void Refund_list_filters_by_state_newest_first_and_drops_refunds_of_other_events()
    {
        var order = Order(lines: Ticket());
        var failed = Refund(order.Id, 22500, RefundState.Failed, minute: 1);
        var manual = Refund(order.Id, 22500, RefundState.ManualRequired, minute: 2);
        var elsewhere = Refund(Guid.NewGuid(), 100, RefundState.Failed);

        TicketSupportReports.RefundList([order], [failed, manual, elsewhere], null)
            .Select(r => r.Refund).Should().Equal(manual, failed);
        TicketSupportReports.RefundList([order], [failed, manual, elsewhere], RefundState.Failed)
            .Should().ContainSingle().Which.Order.Should().BeSameAs(order);
    }

    [HumansFact]
    public void Sales_report_counts_paid_orders_only_and_splits_tickets_from_donations()
    {
        var wave1 = Order(lines: [Ticket(), Donation(17500, TicketDonationPurpose.LowIncomePool)]);
        var lowIncome = Order(lines: Ticket("Ticket: Low income", 12500));
        var unpaid = Order(TicketCheckoutOrderState.Pending, lines: Ticket());

        var report = TicketSupportReports.Sales([wave1, lowIncome, unpaid], [], Madrid);

        report.TicketsSold.Should().Be(2);
        report.TicketRevenueMinor.Should().Be(35000);
        report.DonationsMinor.Should().Be(17500);
        report.ByPrice.Should().BeEquivalentTo(
            [new SalesRow("Ticket: Low income", 1, 12500), new SalesRow("Ticket: Wave 1", 1, 22500)]);
        report.DonationsByPurpose.Should().Equal(new DonationRow(TicketDonationPurpose.LowIncomePool, 1, 17500));
    }

    [HumansFact]
    public void Sales_by_day_uses_the_event_time_zone()
    {
        // 23:30 UTC on 10 January is already 11 January in Madrid.
        var late = Order(paidAt: Instant.FromUtc(2027, 1, 10, 23, 30), lines: [Ticket(), Donation(1000)]);
        var early = Order(paidAt: Instant.FromUtc(2027, 1, 10, 9, 0), lines: Ticket());

        TicketSupportReports.Sales([late, early], [], Madrid).ByDay.Should().Equal(
            new DayRow(new LocalDate(2027, 1, 10), 1, 22500, 0),
            new DayRow(new LocalDate(2027, 1, 11), 1, 22500, 1000));
    }

    [HumansFact]
    public void Sales_report_totals_refunds_by_state()
    {
        var order = Order(lines: Ticket());
        var report = TicketSupportReports.Sales([order],
            [Refund(order.Id, 22500, RefundState.Failed), Refund(order.Id, 22500, RefundState.Succeeded)], Madrid);

        report.RefundsByState.Should().Equal(
            new RefundStateRow(RefundState.Succeeded, 1, 22500),
            new RefundStateRow(RefundState.Failed, 1, 22500));
    }

    [HumansFact]
    public void Support_pages_admit_Board_TicketsAdmin_and_Admin_and_change_nothing()
    {
        // TicketsPolicies.Read: Board reads every page here; nothing on this controller writes, so nothing needs Manage.
        typeof(TicketsSupportAdminController).GetCustomAttribute<AuthorizeAttribute>(inherit: false)!.Policy
            .Should().Be(TicketsPolicies.Read);
        typeof(TicketsSupportAdminController).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Should().AllSatisfy(m => m.GetCustomAttributes<Microsoft.AspNetCore.Mvc.HttpGetAttribute>().Should().NotBeEmpty());
    }
}
