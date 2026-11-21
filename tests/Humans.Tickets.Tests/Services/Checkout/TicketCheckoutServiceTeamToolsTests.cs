using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Email.Contracts;
using Humans.Grants.Contracts;
using Humans.Testing;
using Humans.Tickets.Contracts;
using Humans.Tickets.Domain.Checkout;
using Humans.Users.Contracts;
using NSubstitute;
using Xunit;

namespace Humans.Tickets.Tests.Services.Checkout;

public sealed partial class TicketCheckoutServiceTests
{
    [HumansFact]
    public async Task Void_with_a_refund_refunds_first_then_voids_and_says_so()
    {
        var adult = await PaidAdultAsync();

        (await _service.VoidTicketAsync(adult.Id, "Double charged", 22500, _adminId, Ct)).Refusal.Should().BeNull();

        adult.State.Should().Be(TicketCheckoutTicketState.Void);
        await _backend.Received(1).RefundAsync(Arg.Is<RefundRequest>(r => r.AmountMinor == 22500), Arg.Any<CancellationToken>());
        _repo.Events.Should().Contain(e => e.Kind == TicketCheckoutOrderEventKind.Voided && e.Detail.Contains("225.00 refunded"));
        await _audit.Received(1).LogAsync(TicketsAuditActions.TicketCheckoutTicketVoided, Arg.Any<string>(), adult.OrderId,
            Arg.Is<string>(d => d.Contains("refunding 225.00", StringComparison.Ordinal)), _adminId, _buyerId, AuditSubjectTypes.User);
    }

    [HumansFact]
    public async Task Void_with_no_refund_says_plainly_that_no_money_goes_back()
    {
        var adult = await PaidAdultAsync();

        (await _service.VoidTicketAsync(adult.Id, "Fraud", 0, _adminId, Ct)).Refusal.Should().BeNull();

        await _backend.DidNotReceive().RefundAsync(Arg.Any<RefundRequest>(), Arg.Any<CancellationToken>());
        _repo.Events.Should().Contain(e => e.Kind == TicketCheckoutOrderEventKind.Voided && e.Detail.Contains("no money goes back"));
        await _audit.Received(1).LogAsync(TicketsAuditActions.TicketCheckoutTicketVoided, Arg.Any<string>(), adult.OrderId,
            Arg.Is<string>(d => d.Contains("with no refund", StringComparison.Ordinal)), _adminId, _buyerId, AuditSubjectTypes.User);
    }

    [HumansFact]
    public async Task Void_leaves_the_ticket_working_when_the_refund_is_turned_down_or_too_big()
    {
        var adult = await PaidAdultAsync();

        (await _service.VoidTicketAsync(adult.Id, "Too much", 22501, _adminId, Ct)).Refusal.Should().EndWith("Nothing was voided.");
        _backend.RefundAsync(Arg.Any<RefundRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RefundResult(RefundState.Failed, null, "card expired"));
        (await _service.VoidTicketAsync(adult.Id, "Declined", 22500, _adminId, Ct)).Refusal
            .Should().Contain("card expired").And.EndWith("Nothing was voided.");

        adult.State.Should().Be(TicketCheckoutTicketState.Valid);
        await _audit.DidNotReceive().LogAsync(TicketsAuditActions.TicketCheckoutTicketVoided, Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task Restore_brings_a_voided_ticket_back_as_an_audited_override()
    {
        var adult = await PaidAdultAsync();
        await _service.VoidTicketAsync(adult.Id, "Wrong ticket", 0, _adminId, Ct);

        (await _service.RestoreTicketAsync(adult.Id, "Voided the wrong one", _adminId, Ct)).Refusal.Should().BeNull();

        adult.State.Should().Be(TicketCheckoutTicketState.Valid);
        adult.VoidedAt.Should().BeNull();
        _repo.Events.Should().Contain(e => e.Kind == TicketCheckoutOrderEventKind.Restored && e.ActorUserId == _adminId);
        await _audit.Received(1).LogAsync(TicketsAuditActions.TicketOverride, Arg.Any<string>(), adult.OrderId,
            Arg.Is<string>(d => d.StartsWith("Override:", StringComparison.Ordinal)), _adminId, _buyerId, AuditSubjectTypes.User);
        (await _service.RestoreTicketAsync(adult.Id, "Again", _adminId, Ct)).Refusal.Should().Be("Only a voided ticket can be restored.");
    }

    [HumansFact]
    public async Task Restore_refuses_when_the_holder_already_holds_another_ticket()
    {
        var adult = await PaidAdultAsync();
        await _service.VoidTicketAsync(adult.Id, "Rebought", 0, _adminId, Ct);
        await BuyAndPayAsync(Buy(), _buyerId);

        (await _service.RestoreTicketAsync(adult.Id, "Oops", _adminId, Ct)).Refusal
            .Should().Be("The holder already holds another ticket for this event.");
        adult.State.Should().Be(TicketCheckoutTicketState.Void);
    }

    [HumansFact]
    public async Task Override_rename_moves_a_class_priced_ticket_and_audits_it_as_an_override()
    {
        _grants.GetActiveGrantsAsync(_buyerId, Arg.Any<CancellationToken>())
            .Returns([new ActiveGrantInfo(Guid.NewGuid(), LowIncomeProgram, "Low income", _event.Id, Now)]);
        var ticket = await PaidAdultAsync();
        var newHolder = Guid.NewGuid();
        _provisioning.FindOrCreateUserByEmailAsync("cy@example.com", null, ContactSource.Manual, Arg.Any<CancellationToken>())
            .Returns(new AccountProvisioningResult(newHolder, Created: true));

        (await _service.RenameTicketAsync(ticket.Id, "Cy", "cy@example.com", "Board agreed", true, _adminId, Ct))
            .Refusal.Should().BeNull();

        ticket.HolderUserId.Should().Be(newHolder);
        await _audit.Received(2).LogAsync(TicketsAuditActions.TicketOverride, Arg.Any<string>(), ticket.OrderId,
            Arg.Is<string>(d => d.StartsWith("Override:", StringComparison.Ordinal)), _adminId, Arg.Any<Guid?>(), AuditSubjectTypes.User);
    }

    [HumansFact]
    public async Task Override_companion_goes_past_capacity_and_is_audited_as_an_override()
    {
        var adult = await PaidAdultAsync();
        _salesEvent.Capacity = 1;

        (await _service.IssueCompanionAsync(adult.Id, "Helper Ruiz", null, "Carer", false, _adminId, Ct)).Succeeded.Should().BeFalse();
        (await _service.IssueCompanionAsync(adult.Id, "Helper Ruiz", null, "Carer", true, _adminId, Ct)).Refusal.Should().BeNull();

        _repo.Tickets.Should().ContainSingle(t => t.Kind == TicketHolderKind.Companion);
        await _audit.Received(1).LogAsync(TicketsAuditActions.TicketOverride, Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<string>(), _adminId, _buyerId, AuditSubjectTypes.User);
    }

    [HumansFact]
    public async Task Team_takes_a_ticket_out_of_the_resale_queue_for_its_holder_but_not_while_a_buyer_pays()
    {
        var adult = await OfferedTicketAsync();
        var offer = _repo.Offers.Single();
        offer.State = TicketResaleOfferState.Reserved;
        (await _service.WithdrawOfferForHolderAsync(offer.Id, "Holder asked by phone", _adminId, Ct)).Succeeded.Should().BeFalse();

        offer.State = TicketResaleOfferState.Open;
        (await _service.WithdrawOfferForHolderAsync(offer.Id, "Holder asked by phone", _adminId, Ct)).Refusal.Should().BeNull();

        offer.State.Should().Be(TicketResaleOfferState.Withdrawn);
        adult.State.Should().Be(TicketCheckoutTicketState.Valid);
        await _audit.Received(1).LogAsync(TicketsAuditActions.TicketResaleWithdrawnByTeam, Arg.Any<string>(), adult.OrderId,
            Arg.Any<string>(), _adminId, _buyerId, AuditSubjectTypes.User);
    }

    [HumansFact]
    public async Task Cancelling_the_event_voids_refunds_and_emails_every_paid_order_once()
    {
        _salesEvent.ChildPriceMinor = 5000;
        var first = await BuyAndPayAsync(Buy(donation: 1000) with { ChildNames = ["Kid García"] }, _buyerId);
        var second = await BuyAndPayAsync(Buy(), _otherBuyerId);
        _email.ClearReceivedCalls();

        (await _service.CancelSalesEventAsync(_salesEvent.Id, "Storm", _adminId, Ct)).Refusal.Should().BeNull();
        _salesEvent.Mode.Should().Be(TicketSalesMode.Closed);
        (await _service.CancelSalesEventAsync(_salesEvent.Id, "Again", _adminId, Ct)).Succeeded.Should().BeFalse();
        (await _service.GetCancellationProgressAsync(_salesEvent.Id, Ct))!.ToDo.Should().Be(2);

        await _service.ReconcileOpenOrdersAsync(Ct);
        await _service.ReconcileOpenOrdersAsync(Ct);

        _repo.Tickets.Should().OnlyContain(t => t.State == TicketCheckoutTicketState.Void);
        await _backend.Received(1).RefundAsync(Arg.Is<RefundRequest>(r => r.BackendOrderId == first.BackendOrderId && r.AmountMinor == 27500),
            Arg.Any<CancellationToken>());
        await _backend.Received(1).RefundAsync(Arg.Is<RefundRequest>(r => r.BackendOrderId == second.BackendOrderId && r.AmountMinor == 22500),
            Arg.Any<CancellationToken>());
        await _email.Received(2).SendAsync(Arg.Is<EmailMessage>(m => m.TemplateName == "ticket_event_cancelled"), Arg.Any<CancellationToken>());
        _repo.Events.Count(e => e.Kind == TicketCheckoutOrderEventKind.EventCancelled).Should().Be(2);
        await _audit.Received(1).LogAsync(TicketsAuditActions.TicketSalesEventCancelled, Arg.Any<string>(), _salesEvent.Id,
            Arg.Any<string>(), _adminId, Arg.Any<Guid?>(), Arg.Any<string?>());

        var progress = (await _service.GetCancellationProgressAsync(_salesEvent.Id, Ct))!;
        progress.ToDo.Should().Be(0);
        progress.LiveTickets.Should().Be(0);
        progress.StillRefundableMinor.Should().Be(0);
        progress.DonationsMinor.Should().Be(1000);
        (progress.TicketMoneyMinor - progress.DisputedMinor - progress.RefundedMinor - progress.InFlightMinor - progress.StillRefundableMinor)
            .Should().Be(0);
    }

    [HumansFact]
    public async Task A_refund_the_provider_turns_down_after_cancelling_is_not_tried_again_and_shows_as_still_to_refund()
    {
        await BuyAndPayAsync(Buy(), _buyerId);
        _backend.RefundAsync(Arg.Any<RefundRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RefundResult(RefundState.Failed, null, "card closed"));
        await _service.CancelSalesEventAsync(_salesEvent.Id, "Storm", _adminId, Ct);

        await _service.ReconcileOpenOrdersAsync(Ct);
        await _service.ReconcileOpenOrdersAsync(Ct);

        await _backend.Received(1).RefundAsync(Arg.Any<RefundRequest>(), Arg.Any<CancellationToken>());
        var progress = (await _service.GetCancellationProgressAsync(_salesEvent.Id, Ct))!;
        progress.ToDo.Should().Be(0);
        progress.StillRefundableMinor.Should().Be(22500);
        progress.RefundsSince.Should().ContainSingle(r => r.State == RefundState.Failed && r.AmountMinor == 22500);
    }

    [HumansFact]
    public async Task A_cancelled_event_refuses_setup_changes_resale_and_restores()
    {
        var adult = await PaidAdultAsync();
        await _service.CancelSalesEventAsync(_salesEvent.Id, "Storm", _adminId, Ct);
        await _service.ReconcileOpenOrdersAsync(Ct);

        (await _service.SetResaleOpenAsync(_salesEvent.Id, true, _adminId, Ct)).Succeeded.Should().BeFalse();
        (await _service.RestoreTicketAsync(adult.Id, "Oops", _adminId, Ct)).Refusal.Should().Be("The event is cancelled; its tickets stay void.");
    }

    [HumansFact]
    public async Task Cancelling_waits_for_a_buyer_paying_for_a_returned_ticket()
    {
        var adult = await OfferedTicketAsync();
        _repo.Offers.Single().State = TicketResaleOfferState.Reserved;
        await _service.CancelSalesEventAsync(_salesEvent.Id, "Storm", _adminId, Ct);

        await _service.ReconcileOpenOrdersAsync(Ct);

        adult.State.Should().Be(TicketCheckoutTicketState.Offered);
        (await _service.GetCancellationProgressAsync(_salesEvent.Id, Ct))!.ToDo.Should().Be(1);
    }
}
