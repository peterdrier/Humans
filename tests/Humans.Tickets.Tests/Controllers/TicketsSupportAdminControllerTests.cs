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
using Humans.Auth.Contracts;
using Humans.EarlyEntry.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using System.Security.Claims;
using System.Globalization;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using System.Reflection;
using Humans.Tickets.Services;
using Humans.Base.Extensions;
using Microsoft.AspNetCore.Mvc.Routing;

namespace Humans.Tickets.Tests.Controllers;

public class TicketsSupportAdminControllerTests
{
    private readonly ITicketCheckoutService _checkout = Substitute.For<ITicketCheckoutService>();
    private readonly TicketingSettings _sale = new() { Id = Guid.NewGuid(), EventSettingsId = Guid.NewGuid() };
    private static readonly Instant Now = Instant.FromUtc(2027, 1, 10, 12, 0);

    [HumansTheory]
    [InlineData(false, "79228162514264337593543950335", null, null, false)]
    [InlineData(true, "79228162514264337593543950335", "false", null, false)]
    [InlineData(false, "92233720368547758.08", null, null, false)]
    [InlineData(true, "92233720368547758.08", "false", null, false)]
    [InlineData(false, "1,23", null, null, false)]
    [InlineData(true, "1,23", "false", null, false)]
    [InlineData(false, "1,000.25", null, null, false)]
    [InlineData(true, "1,000.25", "false", null, false)]
    [InlineData(false, "1.23e1", null, 1230L, false)]
    [InlineData(true, "1.23e1", "false", 1230L, false)]
    [InlineData(false, "bad", null, null, false)]
    [InlineData(true, "bad", "false", null, false)]
    [InlineData(false, "0.004", null, null, false)]
    [InlineData(true, "-1", "false", null, false)]
    [InlineData(false, null, null, null, true)]
    [InlineData(true, "1", null, null, true)]
    [InlineData(true, "1", "bad", null, true)]
    [InlineData(false, "1.005", null, 101L, false)]
    [InlineData(true, "1.005", "false", 101L, false)]
    [InlineData(false, "92233720368547758.074", null, long.MaxValue, false)]
    [InlineData(true, null, "true", 0L, false)]
    [InlineData(true, "bad", "true", 0L, false)]
    public async Task Refund_MonetaryInput_DispatchesValidAmount(
        bool voiding, string? amount, string? noRefund, long? expectedMinor, bool badBinding)
    {
        var actorId = Guid.NewGuid();
        var users = Substitute.For<IUserService>();
        users.GetUserInfoAsync(actorId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(UserInfo.Create(new User { Id = actorId }, [], [], null)));
        Controller controller = voiding
            ? new TicketsSupportAdminController(_checkout, Substitute.For<ISettingsService>(), SystemClock.Instance, users, NullLogger<TicketsSupportAdminController>.Instance)
            : new TicketsSalesAdminController(_checkout, null!, Substitute.For<ISettingsService>(),
                Substitute.For<IGrantService>(), Substitute.For<IOrgTimeZone>(), SystemClock.Instance, users, NullLogger<TicketsSalesAdminController>.Instance);
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, actorId.ToString())], "Test")),
        };
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        controller.TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>());
        _checkout.RefundAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<string>(), actorId, Arg.Any<CancellationToken>())
            .Returns(CheckoutMutationResult.Ok);
        _checkout.VoidTicketAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<long>(), actorId, Arg.Any<CancellationToken>())
            .Returns(CheckoutMutationResult.Ok);
        var registrations = new ServiceCollection().AddLogging();
        registrations.AddControllers();
        using var services = registrations.BuildServiceProvider();
        http.RequestServices = services;
        controller.Url = Substitute.For<IUrlHelper>();
        var method = controller.GetType().GetMethod(voiding ? "Void" : "Refund")!;
        var fields = new Dictionary<string, StringValues>(StringComparer.Ordinal);
        if (amount is not null) fields[voiding ? "refund" : "amount"] = amount;
        if (noRefund is not null) fields["noRefund"] = noRefund;
        var args = new List<object?>();
        foreach (var parameter in method.GetParameters())
        {
            if (parameter.Name is "amount" or "refund" or "noRefund")
            {
                args.Add(await BindParameterAsync(controller, parameter, fields));
            }
            else
                args.Add(parameter.Name is "ct" ? Xunit.TestContext.Current.CancellationToken
                    : parameter.Name is "reason" ? "Returned" : Guid.NewGuid());
        }

        var result = await (Task<IActionResult>)method.Invoke(controller, [.. args])!;

        if (badBinding)
            result.Should().BeOfType<BadRequestObjectResult>();
        else
            result.Should().BeOfType<RedirectToActionResult>();
        if (expectedMinor is { } minor)
        {
            if (voiding)
                await _checkout.Received(1).VoidTicketAsync(Arg.Any<Guid>(), "Returned", minor, actorId, CancellationToken.None);
            else
                await _checkout.Received(1).RefundAsync(Arg.Any<Guid>(), minor, "Returned", actorId, CancellationToken.None);
        }
        else
            _checkout.ReceivedCalls().Should().BeEmpty();
    }

    [HumansTheory]
    [InlineData("SetResaleOpen", null, null)]
    [InlineData("SetResaleOpen", "bad", null)]
    [InlineData("SetResaleOpen", "true", true)]
    [InlineData("SetResaleOpen", "false", false)]
    [InlineData("DecideCompanion", null, null)]
    [InlineData("DecideCompanion", "bad", null)]
    [InlineData("DecideCompanion", "true", true)]
    [InlineData("DecideCompanion", "false", false)]
    [InlineData("ScheduledSync", null, false)]
    [InlineData("ScheduledSync", "bad", null)]
    [InlineData("ScheduledSync", "true", true)]
    [InlineData("ScheduledSync", "false", false)]
    public async Task Command_BooleanInput_DispatchesOnlyValidForms(string action, string? posted, bool? expected)
    {
        var actorId = Guid.NewGuid();
        var users = Substitute.For<IUserService>();
        users.GetUserInfoAsync(actorId, Arg.Any<CancellationToken>()).Returns(UserFixtures.MakeUserInfo(actorId));
        var sync = Substitute.For<ITicketSyncService>();
        var scheduledSync = string.Equals(action, "ScheduledSync", StringComparison.Ordinal);
        Controller controller = scheduledSync
            ? new TicketController(null!, sync, null!, null!, users, NullLogger<TicketController>.Instance)
            : new TicketsSupportAdminController(_checkout, Substitute.For<ISettingsService>(), SystemClock.Instance,
                users, NullLogger<TicketsSupportAdminController>.Instance);
        var registrations = new ServiceCollection().AddLogging();
        registrations.AddControllers();
        using var services = registrations.BuildServiceProvider();
        var http = new DefaultHttpContext
        {
            RequestServices = services,
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, actorId.ToString())], "Test")),
        };
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        controller.TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>());
        controller.Url = Substitute.For<IUrlHelper>();
        _checkout.SetResaleOpenAsync(Arg.Any<Guid>(), Arg.Any<bool>(), actorId, Arg.Any<CancellationToken>())
            .Returns(CheckoutMutationResult.Ok);
        _checkout.DecideCompanionRequestAsync(Arg.Any<Guid>(), Arg.Any<bool>(), Arg.Any<string?>(), actorId, Arg.Any<CancellationToken>())
            .Returns(CheckoutMutationResult.Ok);
        var method = controller.GetType().GetMethod(action)!;
        var command = method.GetParameters().Single(p => p.ParameterType == typeof(bool));
        var fields = new Dictionary<string, StringValues>(StringComparer.Ordinal);
        if (posted is not null) fields[command.Name!] = posted;
        var args = new List<object?>();
        foreach (var parameter in method.GetParameters())
            args.Add(parameter == command ? await BindParameterAsync(controller, parameter, fields)
                : parameter.ParameterType == typeof(Guid) ? Guid.NewGuid()
                : parameter.ParameterType == typeof(CancellationToken) ? TestContext.Current.CancellationToken : null);

        var result = await (Task<IActionResult>)method.Invoke(controller, [.. args])!;

        if (expected is not { } value)
        {
            result.Should().BeOfType<BadRequestObjectResult>();
            _checkout.ReceivedCalls().Should().BeEmpty();
            sync.ReceivedCalls().Should().BeEmpty();
        }
        else if (scheduledSync)
        {
            result.Should().BeOfType<RedirectResult>();
            await sync.Received(1).SetScheduledSyncEnabledAsync(value, actorId, Arg.Any<CancellationToken>());
        }
        else
        {
            result.Should().BeOfType<RedirectToActionResult>();
            if (string.Equals(action, "SetResaleOpen", StringComparison.Ordinal))
                await _checkout.Received(1).SetResaleOpenAsync(Arg.Any<Guid>(), value, actorId, Arg.Any<CancellationToken>());
            else
                await _checkout.Received(1).DecideCompanionRequestAsync(Arg.Any<Guid>(), value, null, actorId, Arg.Any<CancellationToken>());
        }
    }

    [HumansTheory]
    [InlineData("en", "donation", "1.005", 101L, false)]
    [InlineData("es", "donation", "1.005", 101L, false)]
    [InlineData("de", "donation", "1.005", 101L, false)]
    [InlineData("it", "donation", "1.005", 101L, false)]
    [InlineData("fr", "donation", "1.005", 101L, false)]
    [InlineData("ca", "donation", "1.005", 101L, false)]
    [InlineData("nl", "donation", "1.005", 101L, false)]
    [InlineData("en", "donation", "1.005e0", 101L, false)]
    [InlineData("en", "donation", "bad", null, false)]
    [InlineData("en", "donation", "79228162514264337593543950336", null, false)]
    [InlineData("en", "donation", "1,23", null, false)]
    [InlineData("en", "donation", "100000", 10000000L, false)]
    [InlineData("en", "donation", "100000.01", null, false)]
    [InlineData("en", "donation", "-1", 0L, false)]
    [InlineData("en", "vipTotalMinor", "bad", null, true)]
    [InlineData("en", "vipTotalMinor", "9223372036854775808", null, true)]
    [InlineData("en", "forSelf", "bad", null, true)]
    [InlineData("en", "acceptTerms", "bad", null, true)]
    [InlineData("en", "marketingOptIn", "bad", null, true)]
    [InlineData("en", "settingsId", "bad", null, true)]
    [InlineData("en", "donation", null, 0L, false)]
    [InlineData("en", "donation", "", 0L, false)]
    [InlineData("en", "forSelf", null, 101L, false)]
    [InlineData("en", "marketingOptIn", null, 101L, false)]
    public async Task Start_CartForm_DispatchesValidInputs(
        string cultureName, string field, string? posted, long? expectedMinor, bool badBinding)
    {
        using var culture = new CultureScope(cultureName);
        var actorId = Guid.NewGuid();
        var users = Substitute.For<IUserService>();
        users.GetUserInfoAsync(actorId, Arg.Any<CancellationToken>()).Returns(UserFixtures.MakeUserInfo(actorId));
        var localizer = Substitute.For<IStringLocalizer<TicketsResource>>();
        localizer[Arg.Any<string>()].Returns(call => new LocalizedString(call.Arg<string>(), call.Arg<string>()));
        var controller = new TicketBuyController(_checkout, Substitute.For<IMagicLinkService>(), Substitute.For<IEarlyEntryService>(),
            users, localizer, NullLogger<TicketBuyController>.Instance);
        var registrations = new ServiceCollection().AddLogging();
        registrations.AddControllers();
        using var services = registrations.BuildServiceProvider();
        var http = new DefaultHttpContext
        {
            RequestServices = services,
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, actorId.ToString())], "Test")),
        };
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        controller.TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>());
        controller.Url = Substitute.For<IUrlHelper>();
        controller.Url.Action(Arg.Any<UrlActionContext>()).Returns($"https://humans.example/Tickets/Buy/Order/{Guid.Empty}");
        StartCheckout? cart = null;
        _checkout.StartCheckoutAsync(Arg.Any<StartCheckout>(), actorId, Arg.Any<CancellationToken>()).Returns(call =>
        {
            cart = call.Arg<StartCheckout>();
            return new StartCheckoutResult(Guid.NewGuid(), null, null);
        });
        var fields = new Dictionary<string, StringValues>(StringComparer.Ordinal)
        {
            ["settingsId"] = Guid.NewGuid().ToString(),
            ["forSelf"] = "true",
            ["acceptTerms"] = "true",
            ["marketingOptIn"] = "true",
            ["donation"] = "1.005",
            ["otherEmails"] = new StringValues(["first@example.test", "second@example.test"]),
            ["childNames"] = new StringValues(["Child One", "Child Two"]),
            ["childTypes"] = new StringValues(["Junior", "Teen"]),
        };
        if (posted is null) fields.Remove(field);
        else fields[field] = posted;
        var method = typeof(TicketBuyController).GetMethod(nameof(TicketBuyController.Start))!;
        var args = new List<object?>();
        foreach (var parameter in method.GetParameters())
            args.Add(parameter.ParameterType == typeof(CancellationToken) ? TestContext.Current.CancellationToken
                : await BindParameterAsync(controller, parameter, fields, CultureInfo.CurrentCulture));

        var result = await (Task<IActionResult>)method.Invoke(controller, [.. args])!;

        if (badBinding) result.Should().BeOfType<BadRequestObjectResult>();
        else result.Should().BeOfType<RedirectToActionResult>();
        if (expectedMinor is { } minor)
        {
            cart.Should().NotBeNull();
            cart!.DonationMinor.Should().Be(minor);
            cart.OtherEmails.Should().Equal("first@example.test", "second@example.test");
            cart.Children.Should().Equal(new ChildEntry("Child One", "Junior"), new ChildEntry("Child Two", "Teen"));
            cart.ForSelf.Should().Be(!string.Equals(field, "forSelf", StringComparison.Ordinal) || posted is not null);
            cart.MarketingOptIn.Should().Be(!string.Equals(field, "marketingOptIn", StringComparison.Ordinal) || posted is not null);
            await _checkout.Received(1).StartCheckoutAsync(cart, actorId, CancellationToken.None);
        }
        else
            _checkout.ReceivedCalls().Should().BeEmpty();
    }

    private static async Task<object?> BindParameterAsync(
        Controller controller, ParameterInfo parameter, Dictionary<string, StringValues> fields, CultureInfo? culture = null)
    {
        var services = controller.HttpContext.RequestServices;
        var metadata = ((ModelMetadataProvider)services.GetRequiredService<IModelMetadataProvider>()).GetMetadataForParameter(parameter);
        var binder = services.GetRequiredService<IModelBinderFactory>().CreateBinder(new ModelBinderFactoryContext
        {
            Metadata = metadata,
            CacheToken = parameter,
        });
        var descriptor = new ControllerParameterDescriptor
        {
            Name = parameter.Name!,
            ParameterType = parameter.ParameterType,
            ParameterInfo = parameter,
        };
        var bound = await services.GetRequiredService<ParameterBinder>().BindModelAsync(controller.ControllerContext, binder,
            new FormValueProvider(BindingSource.Form, new FormCollection(fields), culture ?? CultureInfo.InvariantCulture),
            descriptor, metadata, value: null);
        return bound.IsModelSet ? bound.Model : parameter.ParameterType.IsValueType ? Activator.CreateInstance(parameter.ParameterType) : null;
    }

    [HumansTheory]
    [InlineData("cancel")]
    [InlineData("refund")]
    [InlineData("void")]
    public async Task Mutation_RequestAborted_FinishesCheckout(string action)
    {
        using var request = new CancellationTokenSource();
        var actorId = Guid.NewGuid();
        var users = Substitute.For<IUserService>();
        CancellationToken lookupToken = default, writeToken = default;
        users.GetUserInfoAsync(actorId, Arg.Any<CancellationToken>()).Returns(call =>
        {
            lookupToken = call.ArgAt<CancellationToken>(1);
            return new ValueTask<UserInfo?>(UserInfo.Create(new User { Id = actorId }, [], [], null));
        });
        CheckoutMutationResult Complete(CancellationToken token)
        {
            writeToken = token;
            request.Cancel();
            token.ThrowIfCancellationRequested();
            return CheckoutMutationResult.Ok;
        }
        _checkout.CancelAsync(Arg.Any<Guid>(), actorId, Arg.Any<CancellationToken>())
            .Returns(call => Complete(call.ArgAt<CancellationToken>(2)));
        _checkout.RefundAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<string>(), actorId, Arg.Any<CancellationToken>())
            .Returns(call => Complete(call.ArgAt<CancellationToken>(4)));
        _checkout.VoidTicketAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<long>(), actorId, Arg.Any<CancellationToken>())
            .Returns(call => Complete(call.ArgAt<CancellationToken>(4)));
        Controller controller = action switch
        {
            "cancel" => new TicketBuyController(_checkout, Substitute.For<IMagicLinkService>(),
                Substitute.For<IEarlyEntryService>(), users, Substitute.For<IStringLocalizer<TicketsResource>>(),
                NullLogger<TicketBuyController>.Instance),
            "refund" => new TicketsSalesAdminController(_checkout, null!, Substitute.For<ISettingsService>(),
                Substitute.For<IGrantService>(), Substitute.For<IOrgTimeZone>(), SystemClock.Instance, users, NullLogger<TicketsSalesAdminController>.Instance),
            _ => new TicketsSupportAdminController(_checkout, Substitute.For<ISettingsService>(), SystemClock.Instance, users, NullLogger<TicketsSupportAdminController>.Instance),
        };
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, actorId.ToString())], "Test")),
            RequestAborted = request.Token,
        };
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        controller.TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>());

        var result = controller switch
        {
            TicketBuyController buyer => await buyer.Cancel(Guid.NewGuid(), false, request.Token),
            TicketsSalesAdminController sales => await sales.Refund(Guid.NewGuid(), "10", "Returned", request.Token),
            TicketsSupportAdminController support => await support.Void(Guid.NewGuid(), Guid.NewGuid(), "Returned", "10", false, request.Token),
            _ => throw new InvalidOperationException(),
        };

        result.Should().BeOfType<RedirectToActionResult>();
        lookupToken.Should().Be(request.Token);
        writeToken.CanBeCanceled.Should().BeFalse();
        request.IsCancellationRequested.Should().BeTrue();
    }

    private TicketsSupportAdminController Controller()
    {
        _checkout.GetTicketingSettingsAsync(_sale.Id, Arg.Any<CancellationToken>()).Returns(_sale);
        return new(_checkout, Substitute.For<ISettingsService>(), SystemClock.Instance, Substitute.For<IUserService>(), NullLogger<TicketsSupportAdminController>.Instance);
    }

    [HumansFact]
    public async Task AdminOrder_PresentsTicketsByKindAndRefundsOldestFirst()
    {
        var order = new TicketCheckoutOrder { Id = Guid.NewGuid() };
        var adult = new TicketCheckoutTicket { Id = Guid.NewGuid(), Kind = TicketHolderKind.Adult, IssuedAt = Now };
        var child = new TicketCheckoutTicket { Id = Guid.NewGuid(), Kind = TicketHolderKind.Child, IssuedAt = Now - Duration.FromDays(1) };
        var older = new TicketCheckoutRefund { Id = Guid.NewGuid(), CreatedAt = Now };
        var newer = new TicketCheckoutRefund { Id = Guid.NewGuid(), CreatedAt = Now + Duration.FromDays(1) };
        _checkout.GetOrderDetailAsync(order.Id, Arg.Any<CancellationToken>())
            .Returns(new OrderDetail(order, [newer, older], [], [child, adult]));
        var controller = new TicketsSalesAdminController(_checkout, null!, Substitute.For<ISettingsService>(),
            Substitute.For<IGrantService>(), Substitute.For<IOrgTimeZone>(), SystemClock.Instance, Substitute.For<IUserService>(), NullLogger<TicketsSalesAdminController>.Instance);

        var result = (ViewResult)await controller.Order(order.Id, TestContext.Current.CancellationToken);
        var detail = (OrderDetail)result.Model!;

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
    public async Task Platforms_PresentsKeysInOrdinalOrder()
    {
        _checkout.Platforms.Returns([new("z", false, null!), new("a", true, null!)]);
        var controller = new TicketsSalesAdminController(_checkout, null!, Substitute.For<ISettingsService>(),
            Substitute.For<IGrantService>(), Substitute.For<IOrgTimeZone>(), SystemClock.Instance, Substitute.For<IUserService>(), NullLogger<TicketsSalesAdminController>.Instance);

        var result = (ViewResult)await controller.Platforms(TestContext.Current.CancellationToken);

        ((PlatformsViewModel)result.Model!).Platforms.Select(platform => platform.Key).Should().Equal("a", "z");
    }

    [HumansFact]
    public async Task PlatformStatus_OrdersRowsAndStartsReconciliationOnTheOldestLocalDay()
    {
        var older = new TicketCheckoutOrder { Id = Guid.NewGuid(), CreatedAt = Instant.FromUtc(2027, 1, 9, 23, 30) };
        var newer = new TicketCheckoutOrder { Id = Guid.NewGuid(), CreatedAt = Now + Duration.FromDays(1) };
        var oldRefund = new TicketCheckoutRefund { Id = Guid.NewGuid(), CreatedAt = Now };
        var newRefund = new TicketCheckoutRefund { Id = Guid.NewGuid(), CreatedAt = Now + Duration.FromHours(1) };
        var ledger = new PlatformLedger(new("test", true, null!), [older, newer], [], [older, newer],
            [newRefund, oldRefund], [newRefund, oldRefund]);
        _checkout.GetPlatformLedgerAsync("test", Arg.Any<CancellationToken>()).Returns(ledger);
        var zone = Substitute.For<IOrgTimeZone>();
        zone.Zone.Returns(DateTimeZoneProviders.Tzdb["Europe/Madrid"]);
        var controller = new TicketsPlatformAdminController(_checkout, Substitute.For<ISettingsService>(), zone, Substitute.For<IUserService>());

        var result = (ViewResult)await controller.Index("test", TestContext.Current.CancellationToken);
        var model = (PlatformStatusViewModel)result.Model!;

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
        _checkout.GetOrdersForSettingsAsync(_sale.Id, Arg.Any<CancellationToken>()).Returns([older, newer]);
        _checkout.GetTicketingSettingsAsync(_sale.Id, Arg.Any<CancellationToken>()).Returns(_sale);
        var controller = new TicketsSalesAdminController(_checkout, null!, Substitute.For<ISettingsService>(),
            Substitute.For<IGrantService>(), Substitute.For<IOrgTimeZone>(), SystemClock.Instance, Substitute.For<IUserService>(), NullLogger<TicketsSalesAdminController>.Instance);

        var result = (ViewResult)await controller.Orders(_sale.Id, TestContext.Current.CancellationToken);

        var model = ((TicketingSettings, string, IReadOnlyList<TicketCheckoutOrder>))result.Model!;
        model.Item3.Should().Equal(newer, older);
    }

    [HumansFact]
    public async Task Lookup_PresentsEachOrdersTicketsByIssueTime()
    {
        var order = new TicketCheckoutOrder { Id = Guid.NewGuid(), BuyerName = "Target person" };
        var older = new TicketCheckoutTicket { Id = Guid.NewGuid(), OrderId = order.Id, IssuedAt = Now };
        var newer = new TicketCheckoutTicket { Id = Guid.NewGuid(), OrderId = order.Id, IssuedAt = Now + Duration.FromHours(1) };
        _checkout.ListTicketingSettingsAsync(Arg.Any<CancellationToken>()).Returns([_sale]);
        _checkout.GetOrdersForSettingsAsync(_sale.Id, Arg.Any<CancellationToken>()).Returns([order]);
        _checkout.GetTicketsForSettingsAsync(_sale.Id, Arg.Any<CancellationToken>()).Returns([newer, older]);

        var result = (ViewResult)await Controller().Lookup("Target", TestContext.Current.CancellationToken);

        ((LookupViewModel)result.Model!).Hits.Should().ContainSingle().Which.Tickets.Should().Equal(older, newer);
    }

    [HumansFact]
    public async Task Ticket_PresentsDependentsByIssueTimeAndSelectsTheLatestOffer()
    {
        var order = new TicketCheckoutOrder { Id = Guid.NewGuid() };
        var ticket = new TicketCheckoutTicket { Id = Guid.NewGuid(), TicketingSettingsId = _sale.Id };
        var older = new TicketCheckoutTicket { Id = Guid.NewGuid(), ParentTicketId = ticket.Id, IssuedAt = Now };
        var newer = new TicketCheckoutTicket { Id = Guid.NewGuid(), ParentTicketId = ticket.Id, IssuedAt = Now + Duration.FromHours(1) };
        var oldOffer = new TicketResaleOffer { Id = Guid.NewGuid(), TicketId = ticket.Id, OfferedAt = Now };
        var newOffer = new TicketResaleOffer { Id = Guid.NewGuid(), TicketId = ticket.Id, OfferedAt = Now + Duration.FromHours(1) };
        _checkout.GetOrderDetailAsync(order.Id, Arg.Any<CancellationToken>()).Returns(new OrderDetail(order, [], [], [ticket]));
        _checkout.GetTicketsForSettingsAsync(_sale.Id, Arg.Any<CancellationToken>()).Returns([newer, ticket, older]);
        _checkout.GetOffersForSettingsAsync(_sale.Id, Arg.Any<CancellationToken>()).Returns([newOffer, oldOffer]);

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
        _checkout.GetOrdersForSettingsAsync(_sale.Id, Arg.Any<CancellationToken>()).Returns([order]);
        _checkout.GetRefundsForSettingsAsync(_sale.Id, Arg.Any<CancellationToken>()).Returns([older, newer]);

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
        _checkout.GetOffersForSettingsAsync(_sale.Id, Arg.Any<CancellationToken>()).Returns([sold, newer, withdrawn, older]);

        var result = (ViewResult)await Controller().Resale(_sale.Id, TestContext.Current.CancellationToken);

        ((ResaleViewModel)result.Model!).Rows.Select(row => row.Offer).Should().Equal(older, newer, withdrawn, sold);
    }

    [HumansFact]
    public async Task Report_OrdersPriceGroupsPurposesAndStatesForDisplay()
    {
        var order = new TicketCheckoutOrder
        {
            Id = Guid.NewGuid(),
            State = TicketCheckoutOrderState.Paid,
            PaidAt = Now,
            Lines =
            [
                new() { Kind = OrderLineKind.Donation, DonationPurpose = "Z", AmountMinor = 1 },
                new() { Kind = OrderLineKind.Donation, DonationPurpose = "A", AmountMinor = 1 },
            ],
        };
        TicketCheckoutTicket Ticket(string label) => new() { Id = Guid.NewGuid(), OrderId = order.Id, WaveName = label };
        _checkout.GetOrdersForSettingsAsync(_sale.Id, Arg.Any<CancellationToken>()).Returns([order]);
        _checkout.GetTicketsForSettingsAsync(_sale.Id, Arg.Any<CancellationToken>()).Returns([Ticket("Z"), Ticket("B"), Ticket("A"), Ticket("B")]);
        _checkout.GetRefundsForSettingsAsync(_sale.Id, Arg.Any<CancellationToken>()).Returns(
            [new() { OrderId = order.Id, State = RefundState.Failed }, new() { OrderId = order.Id, State = RefundState.Succeeded }]);
        _checkout.GetOffersForSettingsAsync(_sale.Id, Arg.Any<CancellationToken>()).Returns(
            [new() { State = TicketResaleOfferState.Sold }, new() { State = TicketResaleOfferState.Open }]);

        var result = (ViewResult)await Controller().Report(_sale.Id, TestContext.Current.CancellationToken);
        var report = ((ReportViewModel)result.Model!).Report;

        report.ByPrice.Select(row => row.Label).Should().Equal("B", "A", "Z");
        report.DonationsByPurpose.Select(row => row.Purpose).Should().Equal("A", "Z");
        report.RefundsByState.Select(row => row.State).Should().Equal(RefundState.Succeeded, RefundState.Failed);
        report.ResaleByState.Select(row => row.State).Should().Equal(TicketResaleOfferState.Open, TicketResaleOfferState.Sold);
    }
}
