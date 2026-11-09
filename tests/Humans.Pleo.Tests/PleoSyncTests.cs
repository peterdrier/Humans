using System.Text.Json;
using System.Security.Claims;
using Humans.Base.Constants;
using Humans.Pleo.Controllers;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Pleo.Data;
using Humans.Pleo.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace Humans.Pleo.Tests;

public sealed class PleoSyncTests
{
    private const string LaCantinaCategory = "5131f1d0-c6db-4d8a-95ea-f1e0bcd4f456";
    private static readonly Guid LaCantinaRow = Guid.Parse("f2fe14a1-c8b7-43ea-b6eb-dfb73da28541");

    private readonly Repository _repo;
    private readonly IPleoClient _client = Substitute.For<IPleoClient>();
    private readonly IAuditLogService _audit = Substitute.For<IAuditLogService>();
    private readonly Service _service;
    private List<JsonElement> _entries = [];

    public PleoSyncTests()
    {
        var options = new DbContextOptionsBuilder<PleoDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        using (var ctx = new PleoDbContext(options)) ctx.Database.EnsureCreated(); // applies the category seed
        _repo = new Repository(new TestDbContextFactory<PleoDbContext>(options));

        _client.IsConfigured.Returns(true);
        _client.SearchEntriesAsync(Arg.Any<CancellationToken>()).Returns(_ => _entries);
        _client.SearchAccountsAsync(Arg.Any<CancellationToken>()).Returns(
            [new PleoAccountWire("acc-1", "62900114", "Departments / La Cantina", "h1", false)]);
        _client.ListEmployeesAsync(Arg.Any<CancellationToken>()).Returns(
            [new PleoEmployeeWire("emp-1", "Zak", "Bulpitt ", "NCA003", false)]);
        _client.GetBalanceAsync(Arg.Any<CancellationToken>()).Returns(new PleoBalanceWire("EUR", 1_000_000, 990_000, 0));

        _service = new Service(_repo, _client, new FakeClock(Instant.FromUtc(2026, 11, 3, 12, 0)), _audit,
            NullLogger<Service>.Instance);
    }

    private static JsonElement Entry(
        string id, string family = "CARD_PURCHASE", string? subFamily = "IN_STORE", long minors = 1000,
        long? bill = null, string? category = LaCantinaCategory, string status = "COMPLETED")
    {
        var json = JsonSerializer.Serialize(new
        {
            id,
            family,
            subFamily,
            status,
            employeeId = "emp-1",
            accountId = category,
            settledAt = "2026-07-01T10:00:00Z",
            performedAt = "2026-07-01T10:00:00Z",
            transactionValue = new { currency = "EUR", minors },
            totalBillValue = bill is null ? null : new { currency = "EUR", minors = bill },
            merchant = new { name = "Makro" },
            receiptIds = string.Equals(family, "PLEO_INVOICE", StringComparison.Ordinal) ? Array.Empty<string>() : ["r1"],
        });
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private Task<PleoSyncOutcome> SyncAsync() => _service.SyncAsync(Xunit.TestContext.Current.CancellationToken);

    [HumansFact]
    public async Task Sweep_deletes_rows_Pleo_no_longer_returns()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        _entries = [Entry("a"), Entry("b"), Entry("c")];
        await SyncAsync();

        _entries = [Entry("a", minors: 2500), Entry("c")];
        (await SyncAsync()).Should().Be(PleoSyncOutcome.Synced);

        var mirrored = await _repo.GetEntriesAsync(ct);
        mirrored.Select(e => e.Id).Should().BeEquivalentTo(["a", "c"]);
        mirrored.Single(e => string.Equals(e.Id, "a", StringComparison.Ordinal)).AmountEur.Should().Be(-25.00m);
    }

    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task Bounded_entry_notes_preserve_unicode_and_the_original_raw_payload(bool pairFits)
    {
        var prefix = new string('n', pairFits ? 1998 : 1999);
        var note = prefix + "😀tail";
        var wire = System.Text.Json.Nodes.JsonNode.Parse(Entry("unicode").GetRawText())!;
        wire["note"] = note;
        _entries = [Parse(wire.ToJsonString())];

        await SyncAsync();

        var entry = (await _repo.GetEntriesAsync(Xunit.TestContext.Current.CancellationToken)).Single();
        entry.Note.Should().Be(prefix + (pairFits ? "😀" : ""));
        var encode = () => new System.Text.UTF8Encoding(false, true).GetBytes(entry.Note!);
        encode.Should().NotThrow();
        Parse(entry.Raw).GetProperty("note").GetString().Should().Be(note);
    }

    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task Bounded_sync_errors_preserve_unicode_and_the_original_failure(bool pairFits)
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        _entries = [Entry("existing")];
        await SyncAsync();
        var prefix = new string('e', pairFits ? 1998 : 1999);
        var failure = new IOException(prefix + "😀tail");
        _client.SearchEntriesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<JsonElement>>(failure));

        var action = () => SyncAsync();
        (await action.Should().ThrowAsync<IOException>()).Which.Should().BeSameAs(failure);

        var state = (await _repo.GetSyncStateAsync(ct))!;
        state.SyncStatus.Should().Be(Domain.PleoSyncStatus.Error);
        state.LastError.Should().Be(prefix + (pairFits ? "😀" : ""));
        var encode = () => new System.Text.UTF8Encoding(false, true).GetBytes(state.LastError!);
        encode.Should().NotThrow();
        (await _repo.GetEntriesAsync(ct)).Should().ContainSingle().Which.Id.Should().Be("existing");
    }

    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task Error_state_save_failure_preserves_the_sync_failure_and_releases_the_gate(bool requestAborted)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Xunit.TestContext.Current.CancellationToken);
        Exception failure = requestAborted
            ? new OperationCanceledException(cancellation.Token)
            : new PleoTransientException("Pleo unavailable");
        var saveFailure = new IOException("Cannot save sync diagnostic");
        var repository = Substitute.For<IPleoRepository>();
        repository.GetSyncStateAsync(Arg.Any<CancellationToken>()).Returns(new Domain.PleoSyncState());
        repository.SaveSyncStateAsync(Arg.Any<Domain.PleoSyncState>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            if (call.Arg<Domain.PleoSyncState>().SyncStatus == Domain.PleoSyncStatus.Error)
            {
                call.Arg<CancellationToken>().Should().Be(CancellationToken.None);
                throw saveFailure;
            }
            return Task.CompletedTask;
        });
        _client.SearchEntriesAsync(Arg.Any<CancellationToken>()).Returns(async Task<IReadOnlyList<JsonElement>> (_) =>
        {
            if (requestAborted) await cancellation.CancelAsync();
            throw failure;
        });
        var logger = new CapturingLogger<Service>();
        var service = new Service(repository, _client, new FakeClock(Instant.FromUtc(2026, 11, 3, 12, 0)), _audit, logger);

        var observed = await Xunit.Record.ExceptionAsync(() => service.SyncAsync(cancellation.Token));

        observed.Should().BeSameAs(failure);
        logger.Entries.Should().Contain(entry => entry.Level == LogLevel.Error && ReferenceEquals(entry.Exception, saveFailure));
        await repository.DidNotReceiveWithAnyArgs().ReplaceMirrorAsync(default!, default);
        _client.SearchEntriesAsync(Arg.Any<CancellationToken>()).Returns(_ => _entries);
        (await service.SyncAsync(Xunit.TestContext.Current.CancellationToken)).Should().Be(PleoSyncOutcome.Synced);
    }

    [HumansFact]
    public async Task Wallet_balance_is_the_last_sweeps_current_balance()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        (await _service.GetWalletBalanceAsync(ct)).Should().BeNull();

        await SyncAsync();

        (await _service.GetWalletBalanceAsync(ct)).Should()
            .Be(new Humans.Pleo.Contracts.PleoWalletBalance(10_000m, Instant.FromUtc(2026, 11, 3, 12, 0)));
    }

    [HumansFact]
    public async Task Empty_rows_are_skipped_and_counted()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        _entries =
        [
            Entry("a"),
            Parse("""{"id":null,"family":null,"transactionValue":null}"""),
            Parse("""{"id":"x","family":null}"""),
            Parse("null"),
        ];

        await SyncAsync();

        (await _repo.GetEntriesAsync(ct)).Select(e => e.Id).Should().Equal("a");
        var state = await _repo.GetSyncStateAsync(ct);
        state!.LastCount.Should().Be(1);
        state.SkippedCount.Should().Be(3);
    }

    [HumansFact]
    public async Task Category_mapping_survives_a_sweep()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        _entries = [Entry("a")];
        await SyncAsync();
        var actor = Guid.NewGuid();
        (await _service.LinkCategoryAsync(LaCantinaCategory, LaCantinaRow, actor, ct)).Should().BeNull();
        await _audit.Received(1).LogAsync(PleoAuditActions.PleoCategoryLinked, "PleoCategory", LaCantinaRow,
            Arg.Is<string>(d => d.Contains(LaCantinaCategory, StringComparison.Ordinal)), actor);

        _entries = [Entry("a"), Entry("b")];
        await SyncAsync();

        var categories = await _repo.GetCategoriesAsync(ct);
        categories.Should().HaveCount(13);
        categories.Single(c => c.Id == LaCantinaRow).PleoCategoryId.Should().Be(LaCantinaCategory);
        var settled = await _service.GetSettledEntriesAsync(new LocalDate(2026, 7, 1), new LocalDate(2026, 7, 1), ct);
        settled.Select(e => e.HoldedCode).Should().Equal("62900114", "62900114");
        settled.Should().OnlyContain(e => e.HasReceipt);
    }

    [HumansFact]
    public async Task A_failed_fetch_leaves_the_mirror_and_records_the_error()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        _entries = [Entry("a")];
        await SyncAsync();
        _client.GetBalanceAsync(Arg.Any<CancellationToken>()).Returns<PleoBalanceWire>(
            _ => throw new PleoPermanentException("Pleo balance: 403 Forbidden", 403));
        _entries = [];

        await _service.Invoking(s => s.SyncAsync(ct)).Should().ThrowAsync<PleoPermanentException>();

        (await _repo.GetEntriesAsync(ct)).Should().ContainSingle();
        var state = await _repo.GetSyncStateAsync(ct);
        state!.SyncStatus.Should().Be(Domain.PleoSyncStatus.Error);
        state.LastError.Should().Contain("403");
    }

    [HumansFact]
    public async Task Summary_reconciles_loads_spend_and_refunds_against_Pleos_balance()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        _entries =
        [
            Entry("load", "WALLET", "LOAD", 1_100_000, bill: 1_100_000, category: null),
            Entry("buy", minors: 120_000, bill: -120_000),
            Entry("refund", minors: 20_000, bill: 20_000),
            Entry("fee", "PLEO_INVOICE", "PLEO_INVOICE", 7200, bill: -7200, category: null),
            Entry("pending", minors: 999, status: "PENDING"),
            Entry("odd", "REIMBURSEMENT", null, 500),
        ];
        await SyncAsync();

        var summary = await _service.GetSummaryAsync(ct);
        var r = summary.Reconciliation;

        // 11,000 − 1,200 + 200 − 72 = 9,928 against Pleo's 10,000.
        r.Computed.Should().Be(9_928.00m);
        r.PleoCurrent.Should().Be(10_000.00m);
        r.Difference.Should().Be(72.00m);
        r.Kinds.Single(k => string.Equals(k.Label, "Card purchases", StringComparison.Ordinal)).RefundCount.Should().Be(1);
        r.PendingCount.Should().Be(1);
        r.UnclassifiedCount.Should().Be(1);

        // Category unlinked: all spend is unmapped, and the id is waiting on /Pleo/Categories.
        summary.Unmapped.Spend.Should().Be(1_072.00m);
        // Every test card entry carries a receipt; the Pleo invoice here does not.
        summary.MissingReceipt.Count.Should().Be(1);
        summary.ByAccount.Should().BeEmpty();
        summary.UnlinkedCategoryCount.Should().Be(1);

        await _service.LinkCategoryAsync(LaCantinaCategory, LaCantinaRow, Guid.NewGuid(), ct);
        summary = await _service.GetSummaryAsync(ct);
        summary.ByAccount.Single().Spend.Should().Be(1_000.00m);
        summary.Unmapped.Spend.Should().Be(72.00m);
        summary.UnlinkedCategoryCount.Should().Be(0);
    }

    [HumansFact]
    public async Task Linking_missing_category_id_returns_refusal_without_changing_the_mapping()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], "test"))
        };
        var controller = new PleoController(
            Substitute.For<IUserService>(), _service, NullLogger<PleoController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
            TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>())
        };

        var result = await controller.Link(null!, LaCantinaRow, ct);

        result.Should().BeOfType<RedirectToActionResult>().Which.ActionName.Should().Be(nameof(PleoController.Categories));
        controller.TempData[TempDataKeys.ErrorMessage].Should().Be("That is not a Pleo category id.");
        controller.TempData.ContainsKey(TempDataKeys.SuccessMessage).Should().BeFalse();
        (await _repo.GetCategoriesAsync(ct)).Single(c => c.Id == LaCantinaRow).PleoCategoryId.Should().BeNull();
        _audit.ReceivedCalls().Should().BeEmpty();
    }

    [HumansFact]
    public async Task Linking_refuses_an_id_already_linked_elsewhere()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await _service.LinkCategoryAsync(LaCantinaCategory, LaCantinaRow, Guid.NewGuid(), ct);
        var power = Guid.Parse("ec1e51be-d96b-453f-8318-c79371282045");

        (await _service.LinkCategoryAsync(LaCantinaCategory, power, Guid.NewGuid(), ct))
            .Should().Contain("already linked");
    }

    [HumansFact]
    public async Task Without_a_key_the_sweep_does_nothing()
    {
        _client.IsConfigured.Returns(false);

        (await SyncAsync()).Should().Be(PleoSyncOutcome.NotConfigured);
        await _client.DidNotReceive().SearchEntriesAsync(Arg.Any<CancellationToken>());
    }
}
