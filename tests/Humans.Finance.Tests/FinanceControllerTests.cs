using System.Security.Claims;
using System.Globalization;
using AwesomeAssertions;
using Humans.Finance.Contracts;
using Humans.Finance.Controllers;
using Humans.Finance.Models;
using Humans.Finance.Services;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using NodaTime;
using NSubstitute;
using Xunit;

namespace Humans.Finance.Tests;

/// <summary>
/// The controller owns everything /Finance/Creditors renders that the service does not: which
/// column sorts, which way, the tie-break, and the one place Holded's balance sign is flipped for
/// a human. None of it was covered before, so all of it is asserted here.
/// </summary>
public class FinanceControllerTests
{
    private static readonly Guid Ana = Guid.NewGuid();
    private static readonly Guid Bo = Guid.NewGuid();

    private readonly IHoldedFinanceManagement _finance = Substitute.For<IHoldedFinanceManagement>();
    private readonly IHoldedFinanceAdminService _connector = Substitute.For<IHoldedFinanceAdminService>();
    private readonly IUserService _users = Substitute.For<IUserService>();

    private FinanceController MakeController() =>
        new(_users, _finance, _connector, NullLogger<FinanceController>.Instance)
        {
            ControllerContext = new() { HttpContext = new DefaultHttpContext() }
        };

    /// <summary>A controller wired with a real HttpContext and TempData, for actions
    /// (<see cref="FinanceController.GenerateSepa"/>) that need <c>GetCurrentUserId</c> or
    /// <c>SetError</c> to work rather than throw.</summary>
    private FinanceController MakeControllerWithHttpContext(Guid userId)
    {
        var controller = MakeController();
        var services = new ServiceCollection();
        services.AddLogging();
        var http = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, userId.ToString())], authenticationType: "test")),
        };
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = http,
            ActionDescriptor = new ControllerActionDescriptor
            { ActionName = nameof(FinanceController.GenerateSepa) },
        };
        controller.TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>());
        controller.Url = Substitute.For<IUrlHelper>();
        return controller;
    }

    [HumansTheory]
    [InlineData("blockStart", true)]
    [InlineData("addAll", true)]
    [InlineData("blockStart", false)]
    [InlineData("addAll", false)]
    [InlineData(null, true)]
    public async Task ProvisionHoldedAccounts_InvalidBinding_DoesNotProvision(string? invalidField, bool hasActor)
    {
        var actorId = Guid.NewGuid();
        var controller = MakeControllerWithHttpContext(actorId);
        if (!hasActor) controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity());
        if (invalidField is not null) controller.ModelState.AddModelError(invalidField, "Invalid value.");
        var blockStart = string.Equals(invalidField, "blockStart", StringComparison.Ordinal) ? 0 : 62900100;

        var result = await controller.ProvisionHoldedAccounts(blockStart, false);

        if (!hasActor) Assert.IsType<ChallengeResult>(result);
        else if (invalidField is not null) Assert.IsType<BadRequestObjectResult>(result);
        else Assert.IsType<RedirectToActionResult>(result);
        if (!hasActor || invalidField is not null) Assert.Empty(_connector.ReceivedCalls());
        else await _connector.Received(1).ProvisionAsync(62900100, false, actorId);
    }

    [HumansTheory]
    [InlineData("blockStart", false)]
    [InlineData("addAll", false)]
    [InlineData("", false)]
    [InlineData("", true)]
    public async Task ProvisionHoldedAccounts_requires_posted_block_and_batch_choice(string omitted, bool addAll)
    {
        var actorId = Guid.NewGuid();
        var controller = MakeControllerWithHttpContext(actorId);
        var registrations = new ServiceCollection().AddLogging();
        registrations.AddControllers();
        using var services = registrations.BuildServiceProvider();
        controller.HttpContext.RequestServices = services;
        var fields = new Dictionary<string, StringValues>(StringComparer.Ordinal)
        {
            ["blockStart"] = "62900100", ["addAll"] = addAll.ToString(CultureInfo.InvariantCulture)
        };
        if (omitted.Length > 0) fields.Remove(omitted);
        var arguments = new object[] { 0, false };
        var parameters = typeof(FinanceController).GetMethod(nameof(FinanceController.ProvisionHoldedAccounts))!.GetParameters();
        for (var index = 0; index < parameters.Length; index++)
        {
            var parameter = parameters[index];
            var metadata = ((ModelMetadataProvider)services.GetRequiredService<IModelMetadataProvider>()).GetMetadataForParameter(parameter);
            var descriptor = new ControllerParameterDescriptor
            {
                Name = parameter.Name!, ParameterType = parameter.ParameterType, ParameterInfo = parameter
            };
            var binder = services.GetRequiredService<IModelBinderFactory>().CreateBinder(new ModelBinderFactoryContext
            {
                Metadata = metadata, CacheToken = parameter
            });
            var binding = await services.GetRequiredService<ParameterBinder>().BindModelAsync(controller.ControllerContext, binder,
                new FormValueProvider(BindingSource.Form, new FormCollection(fields), CultureInfo.InvariantCulture),
                descriptor, metadata, value: null);
            if (binding.IsModelSet) arguments[index] = binding.Model!;
        }

        var result = await controller.ProvisionHoldedAccounts((int)arguments[0], (bool)arguments[1]);

        if (omitted.Length > 0)
        {
            result.Should().BeOfType<BadRequestObjectResult>();
            controller.ModelState[omitted]!.Errors.Should().ContainSingle();
            _connector.ReceivedCalls().Should().BeEmpty();
        }
        else
        {
            result.Should().BeOfType<RedirectToActionResult>();
            controller.ModelState.IsValid.Should().BeTrue();
            await _connector.Received(1).ProvisionAsync(62900100, addAll, actorId);
        }
    }

    private static CreditorContactBinding Bound(Guid userId, int? num) =>
        new(userId, $"contact-{userId:N}"[..12], num, CreditorContactSource.Auto);

    private static HoldedCreditorAccountRow Row(
        int num, string name, decimal? balance, params CreditorContactBinding[] bindings) =>
        new(num, name, balance, balance is { } b ? Math.Max(0m, -b) : 0m, bindings);

    private static HoldedCreditorAccountRow RowWithDocs(int num, string name, decimal? balance, int? openDocs) =>
        new(num, name, balance, balance is { } b ? Math.Max(0m, -b) : 0m, [], OpenDocumentCount: openDocs);

    private void NameThem(params (Guid Id, string Burner)[] people) =>
        _users.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(people.ToDictionary(p => p.Id, p => MakeUserInfo(p.Id, p.Burner))
                as IReadOnlyDictionary<Guid, UserInfo>);

    private void Accounts(
        IReadOnlyList<HoldedCreditorAccountRow> rows,
        IReadOnlyList<CreditorContactBinding>? unresolved = null) =>
        _finance.ListCreditorAccountsAsync(Arg.Any<CancellationToken>())
            .Returns((rows, unresolved ?? []));

    private static CreditorsPageVm PageOf(IActionResult result) =>
        result.Should().BeOfType<ViewResult>().Subject.Model
            .Should().BeOfType<CreditorsPageVm>().Subject;

    [HumansFact]
    public async Task Creditors_PropagatesBrowserCancellationDuringMemberNameLookup()
    {
        Accounts([Row(40000001, "Ana", 0m, Bound(Ana, 40000001))]);
        _users.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                call.Arg<CancellationToken>().ThrowIfCancellationRequested();
                return (IReadOnlyDictionary<Guid, UserInfo>)new Dictionary<Guid, UserInfo>();
            });
        var controller = MakeControllerWithHttpContext(Ana);
        (await controller.Creditors(null, null)).Should().BeOfType<ViewResult>();
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        controller.HttpContext.RequestAborted = aborted.Token;

        var read = () => controller.Creditors(null, null);
        await read.Should().ThrowAsync<OperationCanceledException>();
    }

    // ─── Sorting ─────────────────────────────────────────────────────────────────

    [HumansFact]
    public async Task Creditors_DefaultsToAccountNumberAscending()
    {
        Accounts([Row(40000007, "Zoe", -10m), Row(40000002, "Ada", -30m)]);

        var page = PageOf(await MakeController().Creditors(sort: null, dir: null));

        page.SortBy.Should().Be("account");
        page.SortDir.Should().Be("asc");
        page.Accounts.Select(a => a.SupplierAccountNum).Should().Equal(40000002, 40000007);
    }

    [HumansFact]
    public async Task Creditors_UnrecognisedSortAndDir_FallBackToAccountAscending()
    {
        // sort and dir arrive on the query string; anything unrecognised must not reach the switch.
        Accounts([Row(40000007, "Zoe", -10m), Row(40000002, "Ada", -30m)]);

        var page = PageOf(await MakeController().Creditors(sort: "; drop", dir: "DESC"));

        page.SortBy.Should().Be("account");
        page.SortDir.Should().Be("asc"); // "DESC" is not "desc" — the compare is ordinal
        page.Accounts.Select(a => a.SupplierAccountNum).Should().Equal(40000002, 40000007);
    }

    [HumansFact]
    public async Task Creditors_SortByName_OrdersCaseInsensitively()
    {
        Accounts([Row(40000002, "zoe", -1m), Row(40000007, "Ada", -1m)]);

        var page = PageOf(await MakeController().Creditors(sort: "name", dir: "asc"));

        page.Accounts.Select(a => a.Name).Should().Equal("Ada", "zoe");
    }

    [HumansFact]
    public async Task Creditors_SortByBalance_UsesTheDisplayedSign()
    {
        // Contract balances are Holded's Σdebit − Σcredit, so -50 is the *larger* debt.
        Accounts([Row(40000002, "Ada", -50m), Row(40000007, "Bo", -5m)]);

        var page = PageOf(await MakeController().Creditors(sort: "balance", dir: "desc"));

        page.Accounts.Select(a => a.Balance).Should().Equal(50m, 5m);
    }

    [HumansFact]
    public async Task Creditors_SortByMember_PutsUnboundAccountsLast()
    {
        NameThem((Ana, "Ana"));
        Accounts([Row(40000002, "unbound", -1m), Row(40000007, "Ada", -1m, Bound(Ana, 40000007))]);

        var page = PageOf(await MakeController().Creditors(sort: "member", dir: "asc"));

        page.Accounts.Select(a => a.SupplierAccountNum).Should().Equal(40000007, 40000002);
    }

    [HumansFact]
    public async Task Creditors_SortByOpenDocs_OrdersByTheCount_WithUncountedRowsFirstAscending()
    {
        // Ascending puts the "—" rows (no count) ahead of 0, so descending — the view a treasurer
        // picks the next ~10-document test member from — puts the 100+ accounts on top.
        Accounts([RowWithDocs(40000002, "Ada", -1m, 120), RowWithDocs(40000007, "Bo", -1m, 9), RowWithDocs(40000009, "Cy", -1m, null)]);

        var page = PageOf(await MakeController().Creditors(sort: "opendocs", dir: "desc"));

        page.SortBy.Should().Be("opendocs");
        page.Accounts.Select(a => a.OpenDocs).Should().Equal(120, 9, null);
    }

    [HumansFact]
    public async Task Creditors_TiedOnTheSortKey_FallsBackToAccountNumber()
    {
        Accounts([Row(40000009, "same", -1m), Row(40000003, "same", -1m), Row(40000006, "same", -1m)]);

        var page = PageOf(await MakeController().Creditors(sort: "name", dir: "asc"));

        page.Accounts.Select(a => a.SupplierAccountNum).Should().Equal(40000003, 40000006, 40000009);
    }

    // ─── The one sign flip ───────────────────────────────────────────────────────

    [HumansFact]
    public async Task Creditors_ShowsTheBalanceFromTheMembersSide()
    {
        Accounts([Row(40000002, "Ada", -120m), Row(40000003, "Bo", 40m), Row(40000004, "Cy", null)]);

        var page = PageOf(await MakeController().Creditors(sort: null, dir: null));

        page.Accounts[0].Balance.Should().Be(120m);   // org owes Ada
        page.Accounts[1].Balance.Should().Be(-40m);   // Bo owes the org
        page.Accounts[2].Balance.Should().BeNull();   // no cached lines — not zero
    }

    // ─── Members ─────────────────────────────────────────────────────────────────

    [HumansFact]
    public async Task Creditors_NamesEveryBoundMember_IncludingACollision()
    {
        NameThem((Ana, "Ana"), (Bo, "Bo"));
        Accounts([Row(40000002, "shared", -1m, Bound(Ana, 40000002), Bound(Bo, 40000002))]);

        var page = PageOf(await MakeController().Creditors(sort: null, dir: null));

        page.Accounts[0].HasCollision.Should().BeTrue();
        page.Accounts[0].Bindings.Select(b => b.MemberName).Should().Equal("Ana", "Bo");
    }

    [HumansFact]
    public async Task Creditors_UnnamedMember_FallsBackToTheirId()
    {
        _users.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, UserInfo>() as IReadOnlyDictionary<Guid, UserInfo>);
        Accounts([Row(40000002, "x", -1m, Bound(Ana, 40000002))]);

        var page = PageOf(await MakeController().Creditors(sort: null, dir: null));

        page.Accounts[0].Bindings[0].MemberName.Should().Be(Ana.ToString());
    }

    [HumansFact]
    public async Task Creditors_CarriesTheUnresolvedBindingsWithTheirNames()
    {
        NameThem((Bo, "Bo"));
        Accounts([], [Bound(Bo, null)]);

        var page = PageOf(await MakeController().Creditors(sort: null, dir: null));

        page.Accounts.Should().BeEmpty();
        page.Unresolved.Should().ContainSingle().Which.MemberName.Should().Be("Bo");
    }

    // ─── SEPA generation cap ─────────────────────────────────────────────────────

    [HumansTheory]
    [Xunit.InlineData("not-a-number")]
    [Xunit.InlineData("0")]
    [Xunit.InlineData("-5.00")]
    public async Task GenerateSepa_UnparseableOrNonPositiveCap_RefusesWithoutCallingTheService(string cap)
    {
        var controller = MakeControllerWithHttpContext(Ana);

        var result = await controller.GenerateSepa(
            [40000004], new Dictionary<int, string> { [40000004] = "10.00" }, cap,
            Xunit.TestContext.Current.CancellationToken);

        result.Should().BeOfType<RedirectToActionResult>().Subject.ActionName
            .Should().Be(nameof(FinanceController.Creditors));
        controller.TempData[Humans.Base.Constants.TempDataKeys.ErrorMessage].Should().NotBeNull();
        await _connector.DidNotReceive().GenerateSepaPayoutAsync(
            Arg.Any<IReadOnlyList<SepaPayoutSelection>>(), Arg.Any<decimal>(), Arg.Any<Guid>(),
            Arg.Any<CancellationToken>());
    }

    [HumansTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GenerateSepa_InvalidBinding_DoesNotGeneratePartialBatch(bool hasActor)
    {
        var controller = hasActor ? MakeControllerWithHttpContext(Ana) : MakeController();
        var registrations = new ServiceCollection().AddLogging();
        registrations.AddControllers();
        using var services = registrations.BuildServiceProvider();
        var metadata = services.GetRequiredService<IModelMetadataProvider>().GetMetadataForType(typeof(int[]));
        var binder = services.GetRequiredService<IModelBinderFactory>().CreateBinder(new ModelBinderFactoryContext
        {
            Metadata = metadata,
            CacheToken = metadata
        });
        var binding = DefaultModelBindingContext.CreateBindingContext(controller.ControllerContext,
            new FormValueProvider(BindingSource.Form, new FormCollection(new Dictionary<string, StringValues>(StringComparer.Ordinal)
            {
                ["selected"] = new StringValues(["40000004", "not-an-account"])
            }), CultureInfo.InvariantCulture), metadata, bindingInfo: null, modelName: "selected");
        await binder.BindModelAsync(binding);
        var selected = Assert.IsType<int[]>(binding.Result.Model);
        selected.Should().Equal(40000004);
        controller.ModelState.IsValid.Should().BeFalse();
        _connector.GenerateSepaPayoutAsync(Arg.Any<IReadOnlyList<SepaPayoutSelection>>(),
            Arg.Any<decimal>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new SepaPayoutResult("f.xml", "<xml/>", null));

        var result = await controller.GenerateSepa(selected,
            new Dictionary<int, string> { [40000004] = "10.00" }, "100.00",
            Xunit.TestContext.Current.CancellationToken);

        if (hasActor) result.Should().BeOfType<BadRequestObjectResult>();
        else result.Should().BeOfType<ChallengeResult>();
        await _connector.DidNotReceiveWithAnyArgs().GenerateSepaPayoutAsync(default!, default, default, default);
    }

    [HumansFact]
    public async Task GenerateSepa_ValidCap_ParsesInvariantlyAndPassesItToTheService()
    {
        var controller = MakeControllerWithHttpContext(Ana);
        _connector.GenerateSepaPayoutAsync(
            Arg.Any<IReadOnlyList<SepaPayoutSelection>>(), Arg.Any<decimal>(), Arg.Any<Guid>(),
            Arg.Any<CancellationToken>()).Returns(new SepaPayoutResult("f.xml", "<xml/>", null));

        await controller.GenerateSepa(
            [40000004], new Dictionary<int, string> { [40000004] = "10.00" }, "75.00",
            Xunit.TestContext.Current.CancellationToken);

        await _connector.Received(1).GenerateSepaPayoutAsync(
            Arg.Any<IReadOnlyList<SepaPayoutSelection>>(), 75.00m, Ana, Arg.Any<CancellationToken>());
    }

    // ─── Statement ───────────────────────────────────────────────────────────────

    [HumansFact]
    public async Task CreditorStatement_AccountWithNoCachedLedger_IsNotFound()
    {
        _finance.GetCreditorLedgerAsync(40000002, Arg.Any<CancellationToken>())
            .Returns((HoldedCreditorLedger?)null);

        (await MakeController().CreditorStatement(40000002)).Should().BeOfType<NotFoundResult>();
    }

    [HumansFact]
    public async Task CreditorStatement_OrdersLinesNewestFirstThenByEntryThenLine()
    {
        var day1 = Instant.FromUtc(2026, 3, 1, 0, 0);
        var day2 = Instant.FromUtc(2026, 3, 2, 0, 0);
        _finance.GetCreditorLedgerAsync(40000002, Arg.Any<CancellationToken>()).Returns(
            new HoldedCreditorLedger(40000002, -10m, 10m,
            [
                Ledger(entry: 5, line: 1, day1),
                Ledger(entry: 9, line: 2, day2),
                Ledger(entry: 9, line: 1, day2),
                Ledger(entry: 7, line: 1, day2),
            ]));

        var controller = MakeController();
        var result = await controller.CreditorStatement(40000002);
        var model = result.Should().BeOfType<ViewResult>().Subject.Model
            .Should().BeOfType<CreditorStatementVm>().Subject;

        model.OwedBalance.Should().Be(10m);
        model.Lines.Select(r => (r.Line.EntryNumber, r.Line.Line)).Should().Equal((9, 1), (9, 2), (7, 1), (5, 1));
    }

    [HumansFact]
    public async Task CreditorStatement_RunningBalance_AccumulatesOldestFirst_ShownNewestFirst()
    {
        var day1 = Instant.FromUtc(2026, 3, 1, 0, 0);
        var day2 = Instant.FromUtc(2026, 3, 2, 0, 0);
        _finance.GetCreditorLedgerAsync(40000002, Arg.Any<CancellationToken>()).Returns(
            new HoldedCreditorLedger(40000002, -30m, 30m,
            [
                Ledger(entry: 9, line: 1, day2) with { Credit = 0m, Debit = 50m },  // paid out 50
                Ledger(entry: 5, line: 1, day1) with { Credit = 100m }, // owed 100
                Ledger(entry: 7, line: 1, day2) with { Credit = 0m, Debit = 20m },  // paid out 20
            ]));

        var model = (await MakeController().CreditorStatement(40000002))
            .Should().BeOfType<ViewResult>().Subject.Model.Should().BeOfType<CreditorStatementVm>().Subject;

        // Chronological: +100 → 100, −20 → 80, −50 → 30; presented newest first.
        model.Lines.Select(r => (r.Line.EntryNumber, r.RunningBalance)).Should().Equal((9, 30m), (7, 80m), (5, 100m));
        model.Lines[0].RunningBalance.Should().Be(model.OwedBalance);
    }

    [HumansFact]
    public async Task CreditorStatement_MemberOwingTheOrganisation_ShowsNegativeBalance()
    {
        _finance.GetCreditorLedgerAsync(40000002, Arg.Any<CancellationToken>())
            .Returns(new HoldedCreditorLedger(40000002, 25m, 0m, []));

        var result = await MakeController().CreditorStatement(40000002);

        result.Should().BeOfType<ViewResult>().Subject.Model
            .Should().BeOfType<CreditorStatementVm>().Subject.OwedBalance.Should().Be(-25m);
    }

    // ─── Connector index ─────────────────────────────────────────────────────────

    [HumansFact]
    public async Task Holded_RendersTheConnectorOverviewUnchanged()
    {
        // The action is dispatch only — the read model is the service's, and the page has no
        // controller-side assembly to get wrong (nobodies-collective/Humans#1000).
        var vm = new HoldedConnectorVm(
            new HoldedDocSyncVm(null, "Idle", null, 0, null, IsStale: true), 3, [], [], []);
        _connector.GetConnectorOverviewAsync(Arg.Any<CancellationToken>()).Returns(vm);

        var result = await MakeController().Holded(Xunit.TestContext.Current.CancellationToken);

        result.Should().BeOfType<ViewResult>().Subject.Model.Should().BeSameAs(vm);
    }

    // ─── SEPA transfers screen ───────────────────────────────────────────────────

    [HumansFact]
    public async Task Sepa_GroupsByFileNewestFirst_AndOrdersTransfersByAccount()
    {
        var older = Guid.NewGuid();
        var newer = Guid.NewGuid();
        var admin = Guid.NewGuid();
        _connector.GetSepaPayoutsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns((
            new List<SepaPayoutTransferRow>
            {
                Transfer(newer, "new.xml", Stamp(2), admin, Bo, 40000007),
                Transfer(older, "old.xml", Stamp(1), admin, Ana, 40000002),
                Transfer(newer, "new.xml", Stamp(2), admin, Ana, 40000002),
            },
            null, [], null));
        NameThem((Ana, "Ada"), (Bo, "Zoe"), (admin, "Treasurer"));

        var page = SepaPageOf(await MakeController().Sepa(Xunit.TestContext.Current.CancellationToken));

        page.Files.Select(f => f.FileName).Should().Equal("new.xml", "old.xml");
        page.Files[0].GeneratedByName.Should().Be("Treasurer");
        page.Files[0].Transfers.Select(t => t.Row.SupplierAccountNum).Should().Equal(40000002, 40000007);
        page.Files[0].Transfers[0].MemberName.Should().Be("Ada");
    }

    [HumansFact]
    public async Task Sepa_CanceledRead_StopsMemberNameLookup()
    {
        using var cancellation = new CancellationTokenSource();
        _connector.GetSepaPayoutsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns((
            new List<SepaPayoutTransferRow> { Transfer(Guid.NewGuid(), "f.xml", Stamp(1), Ana, Ana, 40000002) },
            null, [], null));
        _users.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                call.Arg<CancellationToken>().ThrowIfCancellationRequested();
                return new Dictionary<Guid, UserInfo>();
            });
        await cancellation.CancelAsync();

        var read = () => MakeController().Sepa(cancellation.Token);

        await read.Should().ThrowAsync<OperationCanceledException>();
    }

    [HumansFact]
    public async Task Sepa_UnavailableReason_ReachesThePageUnchanged()
    {
        _connector.GetSepaPayoutsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((new List<SepaPayoutTransferRow>(), "Sepa:TreasuryAccountId is not configured.",
                (IReadOnlyList<SepaBankMovementVm>)[], (string?)null));

        var page = SepaPageOf(await MakeController().Sepa(Xunit.TestContext.Current.CancellationToken));

        page.UnavailableReason.Should().Be("Sepa:TreasuryAccountId is not configured.");
        page.Files.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Sepa_RendersUnmatchedMovementsPanel()
    {
        var unmatched = new List<SepaBankMovementVm>
        {
            new("mov-1", new LocalDate(2026, 9, 18), 50.00m, "unrecognised text", null,
                "pending", "its text names no creditor account"),
        };
        _connector.GetSepaPayoutsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((new List<SepaPayoutTransferRow>(), (string?)null,
                (IReadOnlyList<SepaBankMovementVm>)unmatched, (string?)null));

        var page = SepaPageOf(await MakeController().Sepa(Xunit.TestContext.Current.CancellationToken));

        page.UnmatchedMovements.Should().BeSameAs(unmatched);
    }

    [HumansFact]
    public async Task Sepa_BankFeedUnreadable_RendersTheBannerAndStillListsFiles()
    {
        _connector.GetSepaPayoutsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns((
            new List<SepaPayoutTransferRow> { Transfer(Guid.NewGuid(), "f.xml", Stamp(1), Ana, Ana, 40000002) },
            (string?)null, (IReadOnlyList<SepaBankMovementVm>)[], "Holded's bank feed did not answer."));
        NameThem((Ana, "Ada"));

        var page = SepaPageOf(await MakeController().Sepa(Xunit.TestContext.Current.CancellationToken));

        page.BankFeedError.Should().Be("Holded's bank feed did not answer.");
        page.Files.Should().ContainSingle();
    }

    [HumansFact]
    public async Task BookSepaTransfer_PassesTheMovementIdThrough()
    {
        var transferId = Guid.NewGuid();
        _connector.BookSepaTransferAsync(transferId, "mov-1", Ana)
            .Returns(new SepaBookingResult(true, "Booked."));
        var controller = MakeControllerWithHttpContext(Ana);

        await controller.BookSepaTransfer(transferId, "mov-1");

        await _connector.Received(1).BookSepaTransferAsync(transferId, "mov-1", Ana);
    }

    [HumansFact]
    public async Task BookSepaFile_PassesTheFileAndMovementIdThrough()
    {
        var fileId = Guid.NewGuid();
        _connector.BookSepaFileAsync(fileId, "mov-batch", Ana)
            .Returns(new SepaBookingResult(true, "Booked."));
        var controller = MakeControllerWithHttpContext(Ana);

        await controller.BookSepaFile(fileId, "mov-batch");

        await _connector.Received(1).BookSepaFileAsync(fileId, "mov-batch", Ana);
    }

    [HumansFact]
    public async Task BookSepaFileMatches_PassesTheFileIdAndToleranceThrough()
    {
        var fileId = Guid.NewGuid();
        _connector.BookSepaFileMatchesAsync(fileId, Ana, 7)
            .Returns(new SepaBookingResult(true, "Booked."));
        var controller = MakeControllerWithHttpContext(Ana);

        await controller.BookSepaFileMatches(fileId, days: 7);

        await _connector.Received(1).BookSepaFileMatchesAsync(fileId, Ana, 7);
    }

    [HumansFact]
    public async Task Sepa_PassesThePostedToleranceToTheServiceAndThePage()
    {
        _connector.GetSepaPayoutsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((new List<SepaPayoutTransferRow>(), null, new List<SepaBankMovementVm>(), null));

        var page = SepaPageOf(await MakeController().Sepa(Xunit.TestContext.Current.CancellationToken, days: 7));

        page.ToleranceDays.Should().Be(7);
        await _connector.Received(1).GetSepaPayoutsAsync(7, Arg.Any<CancellationToken>());
    }

    private static Instant Stamp(int day) => Instant.FromUtc(2026, 8, day, 9, 0);

    private static SepaPayoutTransferRow Transfer(
        Guid fileId, string fileName, Instant generatedAt, Guid generatedBy, Guid userId, int account) =>
        new(Guid.NewGuid(), fileId, fileName, generatedAt, generatedBy,
            userId, account, "c1", "Ana Ruiz", "ES79****789", 12.34m, null, null, null, null, null, null, null);

    private static SepaPayoutsPageVm SepaPageOf(IActionResult result) =>
        result.Should().BeOfType<ViewResult>().Subject.Model
            .Should().BeOfType<SepaPayoutsPageVm>().Subject;

    private static CreditorLedgerLine Ledger(int entry, int line, Instant date) => new()
    {
        EntryNumber = entry,
        Line = line,
        Date = date,
        AccountNum = 40000002,
        Debit = 0m,
        Credit = 1m,
    };

    private static UserInfo MakeUserInfo(Guid id, string burnerName) => new(
        Id: id,
        BurnerName: burnerName,
        IsGdprAnonymized: false,
        PreferredLanguage: "en",
        FallbackPictureUrl: null,
        CreatedAt: Instant.FromUtc(2026, 1, 1, 0, 0),
        LastLoginAt: null,
        LastConsentReminderSentAt: null,
        DeletionRequestedAt: null,
        DeletionScheduledFor: null,
        DeletionEligibleAfter: null,
        UnsubscribedFromCampaigns: false,
        SuppressScheduleChangeEmails: false,
        MagicLinkSentAt: null,
        ContactSource: null,
        ExternalSourceId: null,
        MergedToUserId: null,
        MergedAt: null,
        IdentityEmailColumn: null,
        UserEmails: [],
        EventParticipations: [],
        ExternalLogins: [],
        Profile: null,
        CommunicationPreferences: []);
}
