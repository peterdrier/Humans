using AwesomeAssertions;
using Humans.Settings.Contracts;
using Humans.Base.Interfaces;
using Humans.Grants.Contracts;
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
    public async Task SalesEditor_OrdersGrantProgramsByYearThenOrdinalName()
    {
        var grants = Substitute.For<IGrantService>();
        var older = new GrantProgramSummary(Guid.NewGuid(), "A", 2026, null, "Old event");
        var zed = new GrantProgramSummary(Guid.NewGuid(), "Z", 2027, null, "Current event");
        var upper = new GrantProgramSummary(Guid.NewGuid(), "Alpha", 2027, null, "Current event");
        var lower = new GrantProgramSummary(Guid.NewGuid(), "alpha", 2027, null, "Current event");
        grants.GetProgramSummariesAsync(Arg.Any<CancellationToken>()).Returns([older, lower, zed, upper]);
        var zone = Substitute.For<IOrgTimeZone>();
        zone.Zone.Returns(DateTimeZone.Utc);
        var controller = new TicketsSalesAdminController(_checkout, null!, Substitute.For<ISettingsService>(),
            grants, zone, SystemClock.Instance, Substitute.For<IUserService>());

        var result = (ViewResult)await controller.Edit(null, TestContext.Current.CancellationToken);

        ((SalesEditViewModel)result.Model!).Programs.Should().Equal(upper, zed, lower, older);
    }

    [HumansFact]
    public async Task AdminOrder_PresentsTicketsByKindAndRefundsOldestFirst()
    {
        var order = new TicketCheckoutOrder { Id = Guid.NewGuid() };
        var adult = new TicketCheckoutTicket { Id = Guid.NewGuid(), Kind = TicketHolderKind.Adult, IssuedAt = Now };
        var child = new TicketCheckoutTicket { Id = Guid.NewGuid(), Kind = TicketHolderKind.Child, IssuedAt = Now - Duration.FromDays(1) };
        var older = new TicketCheckoutRefund { Id = Guid.NewGuid(), CreatedAt = Now };
        var newer = new TicketCheckoutRefund { Id = Guid.NewGuid(), CreatedAt = Now + Duration.FromDays(1) };
        _checkout.RefreshOrderAsync(order.Id, Arg.Any<CancellationToken>()).Returns(order);
        _checkout.GetOrderDetailAsync(order.Id, Arg.Any<CancellationToken>())
            .Returns(new CheckoutOrderDetail(order, [newer, older], [], [child, adult]));
        var controller = new TicketsSalesAdminController(_checkout, null!, Substitute.For<ISettingsService>(),
            Substitute.For<IGrantService>(), Substitute.For<IOrgTimeZone>(), SystemClock.Instance, Substitute.For<IUserService>());

        var result = (ViewResult)await controller.Order(order.Id, TestContext.Current.CancellationToken);
        var detail = (CheckoutOrderDetail)result.Model!;

        detail.Tickets.Should().Equal(adult, child);
        detail.Refunds.Should().Equal(older, newer);
    }

    [HumansFact]
    public async Task CompanionRequests_PresentsPendingFirstThenNewestWithinEachGroup()
    {
        CompanionRequestRow Row(TicketCompanionRequestState state, Instant requested) =>
            new(new TicketCompanionRequest { Id = Guid.NewGuid(), State = state, RequestedAt = requested }, "", "", "", Guid.Empty);
        var pendingOld = Row(TicketCompanionRequestState.Pending, Now);
        var pendingNew = Row(TicketCompanionRequestState.Pending, Now + Duration.FromHours(1));
        var approved = Row(TicketCompanionRequestState.Approved, Now + Duration.FromHours(2));
        _checkout.GetCompanionRequestsAsync(_sale.Id, Arg.Any<CancellationToken>()).Returns([approved, pendingOld, pendingNew]);

        var result = (ViewResult)await Controller().Companions(_sale.Id, TestContext.Current.CancellationToken);

        ((CompanionsViewModel)result.Model!).Rows.Should().Equal(pendingNew, pendingOld, approved);
    }

    [HumansFact]
    public async Task Cancellation_PresentsRefundGroupsByState()
    {
        var failed = new RefundStateRow(RefundState.Failed, 1, 100);
        var succeeded = new RefundStateRow(RefundState.Succeeded, 2, 200);
        var progress = new CancellationProgress(null, null, null, 0, 0, 0, 0, 0, 0, 0, 0, 0, [failed, succeeded]);
        _checkout.GetCancellationProgressAsync(_sale.Id, Arg.Any<CancellationToken>()).Returns(progress);

        var result = (ViewResult)await Controller().Cancel(_sale.Id, TestContext.Current.CancellationToken);

        ((CancelViewModel)result.Model!).Progress.RefundsSince.Should().Equal(succeeded, failed);
    }

    [HumansFact]
    public async Task Backends_PresentsKeysInOrdinalOrder()
    {
        _checkout.Backends.Returns([new("z", false, null!), new("a", true, null!)]);
        var controller = new TicketsSalesAdminController(_checkout, null!, Substitute.For<ISettingsService>(),
            Substitute.For<IGrantService>(), Substitute.For<IOrgTimeZone>(), SystemClock.Instance, Substitute.For<IUserService>());

        var result = (ViewResult)await controller.Backends(TestContext.Current.CancellationToken);

        ((BackendsViewModel)result.Model!).Backends.Select(backend => backend.Key).Should().Equal("a", "z");
    }

    [HumansFact]
    public async Task BackendStatus_OrdersRowsAndStartsReconciliationOnTheOldestLocalDay()
    {
        var older = new TicketCheckoutOrder { Id = Guid.NewGuid(), CreatedAt = Instant.FromUtc(2027, 1, 9, 23, 30) };
        var newer = new TicketCheckoutOrder { Id = Guid.NewGuid(), CreatedAt = Now + Duration.FromDays(1) };
        var oldRefund = new TicketCheckoutRefund { Id = Guid.NewGuid(), CreatedAt = Now };
        var newRefund = new TicketCheckoutRefund { Id = Guid.NewGuid(), CreatedAt = Now + Duration.FromHours(1) };
        var ledger = new BackendLedger(new("test", true, null!), [older, newer], [], [older, newer],
            [newRefund, oldRefund], [newRefund, oldRefund]);
        _checkout.GetBackendLedgerAsync("test", Arg.Any<CancellationToken>()).Returns(ledger);
        var zone = Substitute.For<IOrgTimeZone>();
        zone.Zone.Returns(DateTimeZoneProviders.Tzdb["Europe/Madrid"]);
        var controller = new TicketsBackendAdminController(_checkout, Substitute.For<ISettingsService>(), zone, Substitute.For<IUserService>());

        var result = (ViewResult)await controller.Index("test", TestContext.Current.CancellationToken);
        var model = (BackendStatusViewModel)result.Model!;

        model.Ledger.Orders.Should().Equal(newer, older);
        model.Ledger.Stuck.Should().Equal(newer, older);
        model.Ledger.UnsettledRefunds.Should().Equal(oldRefund, newRefund);
        model.Ledger.FailedRefunds.Should().Equal(oldRefund, newRefund);
        model.ReconFrom.Should().Be("2027-01-10");
    }

    [HumansFact]
    public async Task Orders_PresentsNewestFirstWithTheViewsDeclaredTupleType()
    {
        var older = new TicketCheckoutOrder { Id = Guid.NewGuid(), CreatedAt = Now };
        var newer = new TicketCheckoutOrder { Id = Guid.NewGuid(), CreatedAt = Now + Duration.FromHours(1) };
        _checkout.GetOrdersForSalesEventAsync(_sale.Id, Arg.Any<CancellationToken>()).Returns([older, newer]);
        _checkout.GetSalesEventAsync(_sale.Id, Arg.Any<CancellationToken>()).Returns(_sale);
        var controller = new TicketsSalesAdminController(_checkout, null!, Substitute.For<ISettingsService>(),
            Substitute.For<IGrantService>(), Substitute.For<IOrgTimeZone>(), SystemClock.Instance, Substitute.For<IUserService>());

        var result = (ViewResult)await controller.Orders(_sale.Id, TestContext.Current.CancellationToken);

        var model = ((TicketSalesEvent, string, IReadOnlyList<TicketCheckoutOrder>))result.Model!;
        model.Item3.Should().Equal(newer, older);
    }

    [HumansFact]
    public async Task Lookup_PresentsEachOrdersTicketsByIssueTime()
    {
        var order = new TicketCheckoutOrder { Id = Guid.NewGuid(), BuyerName = "Target person" };
        var older = new TicketCheckoutTicket { Id = Guid.NewGuid(), OrderId = order.Id, IssuedAt = Now };
        var newer = new TicketCheckoutTicket { Id = Guid.NewGuid(), OrderId = order.Id, IssuedAt = Now + Duration.FromHours(1) };
        _checkout.GetSalesEventsAsync(Arg.Any<CancellationToken>()).Returns([_sale]);
        _checkout.GetOrdersForSalesEventAsync(_sale.Id, Arg.Any<CancellationToken>()).Returns([order]);
        _checkout.GetTicketsForSalesEventAsync(_sale.Id, Arg.Any<CancellationToken>()).Returns([newer, older]);

        var result = (ViewResult)await Controller().Lookup("Target", TestContext.Current.CancellationToken);

        ((LookupViewModel)result.Model!).Hits.Should().ContainSingle().Which.Tickets.Should().Equal(older, newer);
    }

    [HumansFact]
    public async Task Ticket_PresentsDependentsByIssueTimeAndSelectsTheLatestOffer()
    {
        var order = new TicketCheckoutOrder { Id = Guid.NewGuid() };
        var ticket = new TicketCheckoutTicket { Id = Guid.NewGuid(), SalesEventId = _sale.Id };
        var older = new TicketCheckoutTicket { Id = Guid.NewGuid(), ParentTicketId = ticket.Id, IssuedAt = Now };
        var newer = new TicketCheckoutTicket { Id = Guid.NewGuid(), ParentTicketId = ticket.Id, IssuedAt = Now + Duration.FromHours(1) };
        var oldOffer = new TicketResaleOffer { Id = Guid.NewGuid(), TicketId = ticket.Id, OfferedAt = Now };
        var newOffer = new TicketResaleOffer { Id = Guid.NewGuid(), TicketId = ticket.Id, OfferedAt = Now + Duration.FromHours(1) };
        _checkout.GetOrderDetailAsync(order.Id, Arg.Any<CancellationToken>()).Returns(new CheckoutOrderDetail(order, [], [], [ticket]));
        _checkout.GetTicketsForSalesEventAsync(_sale.Id, Arg.Any<CancellationToken>()).Returns([newer, ticket, older]);
        _checkout.GetOffersForSalesEventAsync(_sale.Id, Arg.Any<CancellationToken>()).Returns([newOffer, oldOffer]);

        var result = (ViewResult)await Controller().Ticket(order.Id, ticket.Id, TestContext.Current.CancellationToken);

        var model = (TicketAdminViewModel)result.Model!;
        model.Dependents.Should().Equal(older, newer);
        model.LatestOffer.Should().BeSameAs(newOffer);
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
