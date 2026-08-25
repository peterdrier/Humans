using System.Security.Claims;
using AwesomeAssertions;
using Humans.Finance.Contracts;
using Humans.Finance.Controllers;
using Humans.Finance.Models;
using Humans.Finance.Services;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;

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

    private readonly IHoldedFinanceService _finance = Substitute.For<IHoldedFinanceService>();
    private readonly IHoldedFinanceAdminService _connector = Substitute.For<IHoldedFinanceAdminService>();
    private readonly IUserServiceRead _users = Substitute.For<IUserServiceRead>();

    private FinanceController MakeController() =>
        new(_users, _finance, _connector, NullLogger<FinanceController>.Instance);

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

    private static CreditorContactBinding Bound(Guid userId, int? num) =>
        new(userId, $"contact-{userId:N}"[..12], num, CreditorContactSource.Auto);

    private static HoldedCreditorAccountRow Row(
        int num, string name, decimal? balance, params CreditorContactBinding[] bindings) =>
        new(num, name, balance, balance is { } b ? Math.Max(0m, -b) : 0m, bindings);

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
        await controller.CreditorStatement(40000002);
        var lines = (IReadOnlyList<CreditorLedgerLine>)controller.ViewBag.Lines;

        lines.Select(l => (l.EntryNumber, l.Line)).Should().Equal((9, 1), (9, 2), (7, 1), (5, 1));
    }

    // ─── Connector index ─────────────────────────────────────────────────────────

    [HumansFact]
    public async Task Holded_RendersTheConnectorOverviewUnchanged()
    {
        // The action is dispatch only — the read model is the service's, and the page has no
        // controller-side assembly to get wrong (nobodies-collective/Humans#1000).
        var vm = new HoldedConnectorVm(
            new HoldedDocSyncVm(null, "Idle", null, 0, null, IsStale: true), 3, [], []);
        _connector.GetConnectorOverviewAsync(Arg.Any<CancellationToken>()).Returns(vm);

        var result = await MakeController().Holded(Xunit.TestContext.Current.CancellationToken);

        result.Should().BeOfType<ViewResult>().Subject.Model.Should().BeSameAs(vm);
    }

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
        ICalToken: null,
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
