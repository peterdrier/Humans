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
/// deposit returns stay with every Store admin, but a <c>Refund</c> is FinanceAdmin/Admin only.
/// Drives the real controller, real <see cref="Service"/> and real <see cref="OrderAuthorizationHandler"/>
/// over a substituted repository.
/// </summary>
public sealed class StoreControllerRecordPaymentTests
{
    private readonly IStoreRepository _repo = Substitute.For<IStoreRepository>();
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
        var service = new Service(
            _repo, Substitute.For<IAuditLogService>(), camps, teams, clock, settings,
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
}
