using AwesomeAssertions;
using Humans.Settings.Contracts;
using Humans.Tickets.Contracts;
using Humans.Tickets.Controllers;
using Humans.Tickets.Domain.Checkout;
using Humans.Tickets.Services.Checkout;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using NSubstitute;
using Xunit;

namespace Humans.Tickets.Tests.Controllers;

public class TicketsSupportAdminControllerTests
{
    private readonly ITicketCheckoutService _checkout = Substitute.For<ITicketCheckoutService>();
    private readonly TicketSalesEvent _sale = new() { Id = Guid.NewGuid(), EventSettingsId = Guid.NewGuid() };
    private static readonly Instant Now = Instant.FromUtc(2027, 1, 10, 12, 0);

    private TicketsSupportAdminController Controller()
    {
        _checkout.GetSalesEventAsync(_sale.Id, Arg.Any<CancellationToken>()).Returns(_sale);
        return new(_checkout, Substitute.For<ISettingsService>(), SystemClock.Instance, Substitute.For<IUserService>());
    }

    [HumansFact]
    public async Task Refunds_PresentsTheNewestRefundFirst()
    {
        var order = new TicketCheckoutOrder { Id = Guid.NewGuid() };
        var older = new TicketCheckoutRefund { Id = Guid.NewGuid(), OrderId = order.Id, CreatedAt = Now };
        var newer = new TicketCheckoutRefund { Id = Guid.NewGuid(), OrderId = order.Id, CreatedAt = Now + Duration.FromHours(1) };
        _checkout.GetOrdersForSalesEventAsync(_sale.Id, Arg.Any<CancellationToken>()).Returns([order]);
        _checkout.GetRefundsForSalesEventAsync(_sale.Id, Arg.Any<CancellationToken>()).Returns([older, newer]);

        var result = (ViewResult)await Controller().Refunds(_sale.Id, null, TestContext.Current.CancellationToken);

        ((RefundsViewModel)result.Model!).Rows.Select(row => row.Refund).Should().Equal(newer, older);
    }

    [HumansFact]
    public async Task Resale_PresentsQueuePositionsThenClosedOffersNewestFirst()
    {
        var newer = new TicketResaleOffer { Id = Guid.NewGuid(), OfferedAt = Now, State = TicketResaleOfferState.Open };
        var older = new TicketResaleOffer { Id = Guid.NewGuid(), OfferedAt = Now - Duration.FromHours(1), State = TicketResaleOfferState.Reserved };
        var sold = new TicketResaleOffer { Id = Guid.NewGuid(), OfferedAt = Now, SoldAt = Now, State = TicketResaleOfferState.Sold };
        var withdrawn = new TicketResaleOffer { Id = Guid.NewGuid(), OfferedAt = Now, WithdrawnAt = Now + Duration.FromHours(1), State = TicketResaleOfferState.Withdrawn };
        _checkout.GetOffersForSalesEventAsync(_sale.Id, Arg.Any<CancellationToken>()).Returns([sold, newer, withdrawn, older]);

        var result = (ViewResult)await Controller().Resale(_sale.Id, TestContext.Current.CancellationToken);

        ((ResaleViewModel)result.Model!).Rows.Select(row => row.Offer).Should().Equal(older, newer, withdrawn, sold);
    }

    [HumansFact]
    public async Task Report_OrdersPriceGroupsPurposesAndStatesForDisplay()
    {
        var order = new TicketCheckoutOrder
        {
            Id = Guid.NewGuid(), State = TicketCheckoutOrderState.Paid, PaidAt = Now,
            Lines =
            [
                new() { Kind = CheckoutLineKind.Donation, DonationPurpose = "Z", AmountMinor = 1 },
                new() { Kind = CheckoutLineKind.Donation, DonationPurpose = "A", AmountMinor = 1 },
            ],
        };
        TicketCheckoutTicket Ticket(string label) => new() { Id = Guid.NewGuid(), OrderId = order.Id, WaveName = label };
        _checkout.GetOrdersForSalesEventAsync(_sale.Id, Arg.Any<CancellationToken>()).Returns([order]);
        _checkout.GetTicketsForSalesEventAsync(_sale.Id, Arg.Any<CancellationToken>()).Returns([Ticket("Z"), Ticket("B"), Ticket("A"), Ticket("B")]);
        _checkout.GetRefundsForSalesEventAsync(_sale.Id, Arg.Any<CancellationToken>()).Returns(
            [new() { OrderId = order.Id, State = RefundState.Failed }, new() { OrderId = order.Id, State = RefundState.Succeeded }]);
        _checkout.GetOffersForSalesEventAsync(_sale.Id, Arg.Any<CancellationToken>()).Returns(
            [new() { State = TicketResaleOfferState.Sold }, new() { State = TicketResaleOfferState.Open }]);

        var result = (ViewResult)await Controller().Report(_sale.Id, TestContext.Current.CancellationToken);
        var report = ((ReportViewModel)result.Model!).Report;

        report.ByPrice.Select(row => row.Label).Should().Equal("B", "A", "Z");
        report.DonationsByPurpose.Select(row => row.Purpose).Should().Equal("A", "Z");
        report.RefundsByState.Select(row => row.State).Should().Equal(RefundState.Succeeded, RefundState.Failed);
        report.ResaleByState.Select(row => row.State).Should().Equal(TicketResaleOfferState.Open, TicketResaleOfferState.Sold);
    }
}
