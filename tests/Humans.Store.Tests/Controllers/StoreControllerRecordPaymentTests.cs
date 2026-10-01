using System.Security.Claims;
using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Base.Constants;
using Humans.Camps.Contracts;
using Humans.Holded.Contracts;
using Humans.Settings.Contracts;
using Humans.Store.Authorization;
using Humans.Store.Contracts;
using Humans.Store.Controllers;
using Humans.Store.Data;
using Humans.Store.Domain;
using Humans.Store.Services;
using Humans.Stripe.Contracts;
using Humans.Teams.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;

namespace Humans.Store.Tests.Controllers;

/// <summary>
/// Server-side enforcement of who may record what on <c>/Store/Order/{id}/RecordPayment</c>:
/// deposit returns stay with every Store admin, a <c>Refund</c> is FinanceAdmin/Admin only, and deleting a payment row is full Admin only.
/// Drives the real controller, real <see cref="Service"/> and real <see cref="OrderAuthorizationHandler"/>
/// over a substituted repository.
/// </summary>
public sealed class StoreControllerRecordPaymentTests
{
    private readonly IStoreRepository _repo = Substitute.For<IStoreRepository>();
    private readonly IAuditLogService _audit = Substitute.For<IAuditLogService>();
    private Service _service = null!;
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Order _order = new()
    {
        Id = Guid.NewGuid(),
        CampSeasonId = Guid.NewGuid(),
        Year = 2026,
        State = OrderState.Open,
        Lines = [],
        Payments = [],
    };

    private StoreController BuildController(string role)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetActiveEventSettingsAsync().Returns(BurnFixtures.Burn(year: 2026, timeZoneId: "Europe/Madrid"));
        var camps = Substitute.For<ICampServiceRead>();
        var teams = Substitute.For<ITeamServiceRead>();
        var clock = new FakeClock(Instant.FromUtc(2026, 9, 1, 10, 0));
        _repo.GetOrderWithLinesAndPaymentsAsync(_order.Id, Arg.Any<CancellationToken>()).Returns(_order);
        _repo.GetAllProductsForYearAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new List<Product>());
        _repo.GetProductsByIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>()).Returns(new List<Product>());
        var service = _service = new Service(
            _repo, _audit, camps, teams, clock, settings,
            Substitute.For<IStripeService>(), Substitute.For<IHoldedClient>(),
            Options.Create(new StoreSectionOptions()), NullLogger<Service>.Instance);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorizationCore();
        services.AddSingleton<IAuthorizationHandler>(new OrderAuthorizationHandler(camps, teams, settings, clock));
        var auth = services.BuildServiceProvider().GetRequiredService<IAuthorizationService>();

        var users = Substitute.For<IUserServiceRead>();
        users.GetUserInfoAsync(_userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(MakeUserInfo(_userId)));
        var localizer = Substitute.For<IStringLocalizer<StoreResource>>();
        localizer[Arg.Any<string>()].Returns(ci => new LocalizedString(ci.Arg<string>(), ci.Arg<string>()));

        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, _userId.ToString()), new Claim(ClaimTypes.Role, role)], "test")),
        };
        return new StoreController(service, camps, auth, users, NullLogger<StoreController>.Instance, localizer)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
            TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>()),
        };
    }

    private static UserInfo MakeUserInfo(Guid id) => new(
        id, "Tester", false, "en", null, Instant.FromUtc(2026, 1, 1, 0, 0), null, null, null, null, null,
        false, false, null, null, null, null, null, null, [], [], [], null, []);

    [HumansFact]
    public async Task StoreAdmin_is_forbidden_to_record_a_refund()
    {
        var controller = BuildController(RoleNames.StoreAdmin);

        var result = await controller.RecordPayment(
            _order.Id, PaymentMethod.Refund, 10m, "re_123", null, TestContext.Current.CancellationToken);

        result.Should().BeOfType<ForbidResult>();
        await _repo.DidNotReceive().AddPaymentAsync(Arg.Any<Payment>(), Arg.Any<CancellationToken>());
    }

    [HumansTheory]
    [InlineData(RoleNames.FinanceAdmin)]
    [InlineData(RoleNames.Admin)]
    public async Task FinanceAdmin_and_Admin_can_record_a_refund(string role)
    {
        var controller = BuildController(role);

        var result = await controller.RecordPayment(
            _order.Id, PaymentMethod.Refund, 10m, "re_123", null, TestContext.Current.CancellationToken);

        result.Should().BeOfType<RedirectToActionResult>();
        await _repo.Received(1).AddPaymentAsync(
            Arg.Is<Payment>(p => p.Method == PaymentMethod.Refund && p.AmountEur == -10m),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task StoreAdmin_can_still_record_a_deposit_return()
    {
        _order.Lines =
        [
            new OrderLine
            {
                Id = Guid.NewGuid(), OrderId = _order.Id, ProductId = Guid.NewGuid(), Qty = 1,
                UnitPriceSnapshot = 5m, VatRateSnapshot = 21m, DepositAmountSnapshot = 30m,
            },
        ];
        var controller = BuildController(RoleNames.StoreAdmin);

        var result = await controller.RecordPayment(
            _order.Id, PaymentMethod.DepositReturn, 30m, null, null, TestContext.Current.CancellationToken);

        result.Should().BeOfType<RedirectToActionResult>();
        await _repo.Received(1).AddPaymentAsync(
            Arg.Is<Payment>(p => p.Method == PaymentMethod.DepositReturn && p.AmountEur == 30m),
            Arg.Any<CancellationToken>());
    }

    private Payment AddPayment(decimal amount, PaymentMethod method)
    {
        var payment = new Payment
        {
            Id = Guid.NewGuid(), OrderId = _order.Id, AmountEur = amount, Method = method,
            Status = PaymentStatus.Paid, ExternalRef = "ref-1", ReceivedAt = Instant.FromUtc(2026, 8, 1, 9, 30),
        };
        _order.Payments.Add(payment);
        return payment;
    }

    [HumansTheory]
    [InlineData(RoleNames.StoreAdmin)]
    [InlineData(RoleNames.FinanceAdmin)]
    public async Task Only_full_Admin_may_delete_a_payment(string role)
    {
        var payment = AddPayment(-3m, PaymentMethod.Refund);
        var controller = BuildController(role);

        var result = await controller.DeletePayment(_order.Id, payment.Id, TestContext.Current.CancellationToken);

        result.Should().BeOfType<ForbidResult>();
        await _repo.DidNotReceive().DeletePaymentAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _audit.DidNotReceive().LogAsync(
            AuditAction.StorePaymentDeleted, Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<string>(),
            Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task Admin_deleting_a_payment_removes_the_row_audits_it_and_the_balance_follows()
    {
        _order.Lines =
        [
            new OrderLine
            {
                Id = Guid.NewGuid(), OrderId = _order.Id, ProductId = Guid.NewGuid(), Qty = 1,
                UnitPriceSnapshot = 100m, VatRateSnapshot = 0m,
            },
        ];
        AddPayment(100m, PaymentMethod.Stripe);
        var mistaken = AddPayment(-3m, PaymentMethod.Refund);
        _repo.DeletePaymentAsync(mistaken.Id, Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(_ => _order.Payments.Remove(mistaken));
        var controller = BuildController(RoleNames.Admin);
        (await _service.GetOrderAsync(_order.Id, TestContext.Current.CancellationToken))!.BalanceEur.Should().Be(3m);

        var result = await controller.DeletePayment(_order.Id, mistaken.Id, TestContext.Current.CancellationToken);

        result.Should().BeOfType<RedirectToActionResult>();
        await _repo.Received(1).DeletePaymentAsync(mistaken.Id, Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync(
            AuditAction.StorePaymentDeleted, AuditEntityTypes.Payment, mistaken.Id,
            Arg.Is<string>(d => d.Contains("Refund") && d.Contains("-3.00") && d.Contains("Paid")
                && d.Contains(_order.Id.ToString()) && d.Contains("ref-1") && d.Contains("2026-08-01")),
            _userId, _order.Id, AuditEntityTypes.Order);
        (await _service.GetOrderAsync(_order.Id, TestContext.Current.CancellationToken))!.BalanceEur.Should().Be(0m);
    }

    [HumansFact]
    public async Task Deleting_a_payment_that_is_not_on_the_order_changes_nothing()
    {
        var controller = BuildController(RoleNames.Admin);

        var result = await controller.DeletePayment(_order.Id, Guid.NewGuid(), TestContext.Current.CancellationToken);

        result.Should().BeOfType<RedirectToActionResult>();
        await _repo.DidNotReceive().DeletePaymentAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }
}
