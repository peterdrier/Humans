using System.Text.Json;
using AwesomeAssertions;
using Humans.Backdoor.Controllers;
using Humans.Backdoor.Filters;
using Humans.Store.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using NSubstitute;

namespace Humans.Backdoor.Tests.Controllers;

/// <summary>
/// The parsing and formatting the machine Store accounting API does on top of
/// <see cref="IStoreAccountingRead"/> (peterdrier/Humans#1719). Everything below is controller
/// work: the service is a substitute throughout.
/// </summary>
public class BackdoorStoreControllerTests
{
    private readonly IStoreAccountingRead _store = Substitute.For<IStoreAccountingRead>();
    private readonly BackdoorStoreController _sut;

    public BackdoorStoreControllerTests() =>
        _sut = new BackdoorStoreController(_store, Substitute.For<IUserServiceRead>());

    [HumansFact]
    public void Every_route_hangs_off_the_api_key_filter()
    {
        var filter = typeof(BackdoorStoreController)
            .GetCustomAttributes(typeof(ServiceFilterAttribute), inherit: false)
            .Cast<ServiceFilterAttribute>()
            .Single();

        filter.ServiceType.Should().Be(typeof(BackdoorApiKeyAuthFilter));
    }

    [HumansFact]
    public async Task OrderLines_without_a_year_is_a_BadRequest()
    {
        var result = await _sut.OrderLines(null, Xunit.TestContext.Current.CancellationToken);

        result.Should().BeOfType<BadRequestObjectResult>();
        await _store.DidNotReceive().GetOrderLinesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Payments_without_a_year_is_a_BadRequest()
    {
        var result = await _sut.Payments(null, Xunit.TestContext.Current.CancellationToken);

        result.Should().BeOfType<BadRequestObjectResult>();
        await _store.DidNotReceive().GetPaymentsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task OrderLines_projects_every_field_with_enums_as_strings()
    {
        var orderId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        _store.GetOrderLinesAsync(2026, Arg.Any<CancellationToken>()).Returns([
            new AccountingOrderLineDto(
                2026, orderId, OrderCounterpartyType.Team, "Ice Crew", null, null, null,
                OrderState.Open, null, lineId, productId, "Ice", 75900002,
                3, 4m, 10m, 13.20m, 12m, 1.20m, 0m, Instant.FromUtc(2026, 6, 1, 9, 30)),
        ]);

        var result = await _sut.OrderLines(2026, Xunit.TestContext.Current.CancellationToken);

        var json = JsonSerializer.Serialize(result.Should().BeOfType<OkObjectResult>().Subject.Value);
        json.Should().Contain(@"""year"":2026");
        json.Should().Contain($@"""orderId"":""{orderId}""");
        json.Should().Contain(@"""counterpartyType"":""Team""");
        json.Should().Contain(@"""counterpartyLabel"":""Ice Crew""");
        json.Should().Contain(@"""counterpartyName"":null");
        json.Should().Contain(@"""orderState"":""Open""");
        json.Should().Contain(@"""issuedInvoiceNumber"":null");
        json.Should().Contain($@"""lineId"":""{lineId}""");
        json.Should().Contain(@"""productName"":""Ice""");
        json.Should().Contain(@"""holdedRevenueAccountNum"":75900002");
        json.Should().Contain(@"""qty"":3");
        json.Should().Contain(@"""unitPrice"":4");
        json.Should().Contain(@"""vatRatePercent"":10");
        json.Should().Contain(@"""lineGross"":13.20");
        json.Should().Contain(@"""lineNet"":12");
        json.Should().Contain(@"""lineVat"":1.20");
        json.Should().Contain(@"""depositAmount"":0");
        json.Should().Contain(@"""addedAt"":""2026-06-01T09:30:00Z""");
    }

    [HumansFact]
    public async Task Payments_projects_every_field_and_keeps_refunds_negative()
    {
        var orderId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();
        _store.GetPaymentsAsync(2026, Arg.Any<CancellationToken>()).Returns([
            new AccountingPaymentDto(
                2026, orderId, OrderCounterpartyType.Camp, "Camp Alpha", paymentId,
                -10m, "Manual", null, "refund-1", Instant.FromUtc(2026, 6, 4, 0, 0)),
        ]);

        var result = await _sut.Payments(2026, Xunit.TestContext.Current.CancellationToken);

        var json = JsonSerializer.Serialize(result.Should().BeOfType<OkObjectResult>().Subject.Value);
        json.Should().Contain(@"""counterpartyType"":""Camp""");
        json.Should().Contain(@"""counterpartyLabel"":""Camp Alpha""");
        json.Should().Contain($@"""paymentId"":""{paymentId}""");
        json.Should().Contain(@"""amountEur"":-10");
        json.Should().Contain(@"""method"":""Manual""");
        json.Should().Contain(@"""stripePaymentIntentId"":null");
        json.Should().Contain(@"""externalRef"":""refund-1""");
        json.Should().Contain(@"""receivedAt"":""2026-06-04T00:00:00Z""");
    }
}
