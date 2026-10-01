using AwesomeAssertions;
using Humans.Store.Contracts;
using Humans.Store.Domain;
using Humans.Store.Models;
using Humans.Store.Services.Dtos;
using NodaTime;
using Xunit;

namespace Humans.Store.Tests.Services;

/// <summary>
/// The order page's last summary box ("Balance owed" vs "Refund amount") and its
/// "Deposits returned" box, as the view reads them from <see cref="OrderViewModel"/>.
/// </summary>
public class OrderViewModelBalanceBoxTests
{
    private static OrderViewModel Vm(decimal balance, params OrderPaymentDto[] payments) => new()
    {
        Order = new OrderDto(
            Guid.NewGuid(), Guid.NewGuid(), null, OrderCounterpartyType.Camp, "Camp", 2026, OrderState.Open,
            null, null, null, null, null, null, [], payments,
            0m, 0m, 0m, 0m, balance, Instant.FromUtc(2026, 1, 1, 0, 0)),
    };

    private static OrderPaymentDto Payment(decimal amount, PaymentMethod method, PaymentStatus status = PaymentStatus.Paid) =>
        new(amount, method, status, null, null, Instant.FromUtc(2026, 1, 1, 0, 0), null);

    [HumansFact]
    public void Camp_owing_the_org_reads_balance_owed_with_the_amount()
    {
        var vm = Vm(balance: 120m);

        vm.IsRefundDue.Should().BeFalse();
        vm.BalanceBoxAmountEur.Should().Be(120m);
    }

    [HumansFact]
    public void Zero_balance_keeps_the_balance_owed_label()
    {
        var vm = Vm(balance: 0m);

        vm.IsRefundDue.Should().BeFalse();
        vm.BalanceBoxAmountEur.Should().Be(0m);
    }

    [HumansFact]
    public void Org_owing_the_camp_reads_refund_amount_as_a_positive_figure()
    {
        var vm = Vm(balance: -30m);

        vm.IsRefundDue.Should().BeTrue();
        vm.BalanceBoxAmountEur.Should().Be(30m);
    }

    [HumansFact]
    public void Deposits_returned_sums_only_paid_deposit_returns()
    {
        var vm = Vm(
            balance: 0m,
            Payment(20m, PaymentMethod.DepositReturn),
            Payment(10m, PaymentMethod.DepositReturn),
            Payment(5m, PaymentMethod.DepositReturn, PaymentStatus.Pending),
            Payment(100m, PaymentMethod.Stripe),
            Payment(-15m, PaymentMethod.Refund));

        vm.DepositsReturnedEur.Should().Be(30m);
    }
}
