using AwesomeAssertions;
using Humans.Store.Contracts;
using Humans.Store.Domain;
using Humans.Store.Services.Dtos;
using NodaTime;

namespace Humans.Store.Tests.Services;

/// <summary>
/// The order-state halves of the Delete and IssueInvoice affordances, read by the order page from
/// <see cref="OrderDto"/> rather than worked out in the controller.
/// </summary>
public class OrderDtoStateTests
{
    private static readonly Instant At = Instant.FromUtc(2026, 1, 1, 0, 0);

    private static OrderDto Order(
        OrderState state = OrderState.Open, decimal balance = 0m, int lines = 0, int payments = 0)
    {
        var id = Guid.NewGuid();
        var lineDtos = Enumerable.Range(0, lines)
            .Select(_ => new OrderLineDto(Guid.NewGuid(), id, Guid.NewGuid(), "Ice", 1,
                10m, 21m, null, At, 10m, 2.1m, 0m, 12.1m))
            .ToList();
        var paymentDtos = Enumerable.Range(0, payments)
            .Select(_ => new OrderPaymentDto(Guid.NewGuid(), 5m, PaymentMethod.BankTransfer,
                PaymentStatus.Paid, null, null, At, null))
            .ToList();
        return new OrderDto(id, Guid.NewGuid(), null, OrderCounterpartyType.Camp, "Camp", 2026, state,
            null, null, null, null, null, null, lineDtos, paymentDtos, 0m, 0m, 0m, 0m, balance, At);
    }

    [HumansFact]
    public void Open_empty_zero_balance_order_is_deletable() =>
        Order().IsDeletable.Should().BeTrue();

    [HumansFact]
    public void Order_with_nonzero_balance_is_not_deletable() =>
        Order(balance: 12.1m).IsDeletable.Should().BeFalse();

    [HumansFact]
    public void Order_with_any_payment_is_not_deletable() =>
        Order(payments: 1).IsDeletable.Should().BeFalse();

    [HumansFact]
    public void Invoiced_order_is_neither_deletable_nor_issuable()
    {
        var order = Order(OrderState.InvoiceIssued, lines: 1);

        order.IsDeletable.Should().BeFalse();
        order.IsInvoiceIssuable.Should().BeFalse();
    }

    [HumansFact]
    public void Open_order_with_lines_is_issuable() =>
        Order(lines: 1).IsInvoiceIssuable.Should().BeTrue();

    [HumansFact]
    public void Open_order_without_lines_is_not_issuable() =>
        Order().IsInvoiceIssuable.Should().BeFalse();
}
