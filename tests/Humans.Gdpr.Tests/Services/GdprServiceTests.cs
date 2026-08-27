using AwesomeAssertions;
using Humans.Gdpr.Contracts;
using Humans.Gdpr.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;

namespace Humans.Gdpr.Tests.Services;

public class GdprServiceTests
{
    private static readonly Instant FixedNow = Instant.FromUtc(2026, 4, 15, 10, 30);

    private static GdprService CreateService(params IUserDataContributor[] contributors) =>
        new(
            contributors,
            new FakeClock(FixedNow),
            NullLogger<GdprService>.Instance);

    [HumansFact]
    public async Task ExportForUserAsync_StampsExportedAtFromClock()
    {
        var service = CreateService(new FakeContributor("Profile", new { Name = "Jane" }));

        var export = await service.ExportForUserAsync(Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        export.ExportedAt.Should().Be("2026-04-15T10:30:00Z");
    }

    [HumansFact]
    public async Task ExportForUserAsync_MergesSlicesKeyedBySectionName()
    {
        var profile = new { Name = "Jane", City = "Barcelona" };
        var consents = new[] { new { Document = "Code of Conduct" } };
        var service = CreateService(
            new FakeContributor("Profile", profile),
            new FakeContributor("Consents", consents));

        var export = await service.ExportForUserAsync(Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        export.Sections.Should().HaveCount(2);
        export.Sections["Profile"].Should().BeSameAs(profile);
        export.Sections["Consents"].Should().BeSameAs(consents);
    }

    [HumansFact]
    public async Task ExportForUserAsync_DropsNullSlices()
    {
        var service = CreateService(
            new FakeContributor("Profile", new { Name = "Jane" }),
            new FakeContributor("Applications", (object?)null));

        var export = await service.ExportForUserAsync(Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        export.Sections.Should().ContainKey("Profile");
        export.Sections.Should().NotContainKey("Applications");
    }

    [HumansFact]
    public async Task ExportForUserAsync_PassesUserIdAndCancellationTokenToEveryContributor()
    {
        var userId = Guid.NewGuid();
        using var cts = new CancellationTokenSource();
        var first = new FakeContributor("A", new object());
        var second = new FakeContributor("B", new object());
        var service = CreateService(first, second);

        await service.ExportForUserAsync(userId, cts.Token);

        first.CalledWithUserId.Should().Be(userId);
        first.CalledWithToken.Should().Be(cts.Token);
        second.CalledWithUserId.Should().Be(userId);
        second.CalledWithToken.Should().Be(cts.Token);
    }

    [HumansFact]
    public async Task ExportForUserAsync_FailsLoudlyOnDuplicateSectionName()
    {
        var service = CreateService(
            new FakeContributor("Profile", new { A = 1 }),
            new FakeContributor("Profile", new { B = 2 }));

        var act = async () => await service.ExportForUserAsync(Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Profile*");
    }

    [HumansFact]
    public async Task ExportForUserAsync_PropagatesContributorFailure()
    {
        var boom = new InvalidOperationException("boom");
        var service = CreateService(
            new FakeContributor("Profile", new { A = 1 }),
            new FakeContributor("Applications", boom));

        var act = async () => await service.ExportForUserAsync(Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");
    }

    [HumansFact]
    public async Task ExportForUserAsync_WithNoContributors_ReturnsEmptySectionBag()
    {
        var service = CreateService();

        var export = await service.ExportForUserAsync(Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        export.Sections.Should().BeEmpty();
        export.ExportedAt.Should().NotBeNullOrEmpty();
    }

    [HumansFact]
    public async Task ExportForUserAsync_FlattensMultipleSlicesFromOneContributor()
    {
        var service = CreateService(new FakeContributor(
            new UserDataSlice("Profile", new { Name = "Jane" }),
            new UserDataSlice("ContactFields", new[] { new { Field = "email" } }),
            new UserDataSlice("Languages", new[] { new { Code = "es" } })));

        var export = await service.ExportForUserAsync(Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        export.Sections.Should().HaveCount(3);
        export.Sections.Should().ContainKey("Profile");
        export.Sections.Should().ContainKey("ContactFields");
        export.Sections.Should().ContainKey("Languages");
    }

    [HumansFact]
    public async Task ExportForUserAsync_EmptyCollectionSliceSurvivesAsEmptyList()
    {
        // Empty collections MUST round-trip to "[]" in the JSON: a collection key
        // is always present even when the user has no records, and downstream
        // consumers depend on that.
        var emptyConsents = Array.Empty<object>();
        var service = CreateService(
            new FakeContributor("Profile", new { Name = "Jane" }),
            new FakeContributor("Consents", emptyConsents));

        var export = await service.ExportForUserAsync(Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        export.Sections.Should().ContainKey("Consents",
            "an empty collection slice must NOT be dropped by the orchestrator");
        export.Sections["Consents"].Should().BeSameAs(emptyConsents);
    }

    [HumansFact]
    public async Task ExportForUserAsync_EmptyCollectionSerializesToEmptyArray()
    {
        var emptyConsents = new List<object>();
        var service = CreateService(
            new FakeContributor("Profile", new { Name = "Jane" }),
            new FakeContributor("Consents", emptyConsents));

        var export = await service.ExportForUserAsync(Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        // Flatten into the shape the controllers serialize
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["ExportedAt"] = export.ExportedAt
        };
        foreach (var (section, data) in export.Sections)
        {
            payload[section] = data;
        }

        var json = System.Text.Json.JsonSerializer.Serialize(payload);
        json.Should().Contain("\"Consents\":[]",
            "empty collection slices must serialize as '[]' in the downloaded JSON");
    }

    [HumansFact]
    public async Task ExportForUserAsync_CallsContributorsOneAtATime()
    {
        var log = new ContributorCallLog();
        var service = CreateService(
            new ProbeContributor("A", log),
            new ProbeContributor("B", log),
            new ProbeContributor("C", log));

        await service.ExportForUserAsync(Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        log.MaxConcurrent.Should().Be(1,
            "the fan-out is sequential — a Task.WhenAll would overlap contributors, and one at " +
            "a time is what keeps failure attribution and log order plain");
        log.Order.Should().Equal("A", "B", "C");
    }

    // ==========================================================================
    // EraseForUserAsync (Article 17 fan-out)
    // ==========================================================================

    [HumansFact]
    public async Task EraseForUserAsync_RunsEveryContributor()
    {
        var a = new RecordingContributor("Issues");
        var b = new RecordingContributor("Consents");
        var service = CreateService(a, b);
        var id = Guid.NewGuid();

        await service.EraseForUserAsync(id, Xunit.TestContext.Current.CancellationToken);

        a.ErasedIds.Should().Equal(id);
        b.ErasedIds.Should().Equal(id);
    }

    [HumansFact]
    public async Task EraseForUserAsync_ErasesAccountIdentityLast()
    {
        // Sections that must reach an external processor (the Workspace suspend) need the
        // human's addresses, which the Account contributor is about to drop. Registration
        // order is Account-first here on purpose: ordering is derived from the declaration.
        var order = new List<string>();
        var account = new RecordingContributor(GdprExportSections.Account, order);
        var section = new RecordingContributor(GdprExportSections.Issues, order);
        var service = CreateService(account, section);

        await service.EraseForUserAsync(Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        order.Should().Equal(GdprExportSections.Issues, GdprExportSections.Account);
    }

    [HumansFact]
    public async Task EraseForUserAsync_PropagatesContributorFailureAndStopsBeforeAccount()
    {
        // A throwing contributor aborts the run — the Account identity collapse never
        // happens, so the caller's deletion markers stay set and tomorrow's job retries.
        var boom = new RecordingContributor("Issues") { Throw = new InvalidOperationException("boom") };
        var account = new RecordingContributor(GdprExportSections.Account);
        var service = CreateService(boom, account);

        var act = async () => await service.EraseForUserAsync(
            Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");
        account.ErasedIds.Should().BeEmpty();
    }

    private sealed class RecordingContributor(string section, List<string>? order = null) : IUserDataContributor
    {
        public Exception? Throw { get; init; }
        public List<Guid> ErasedIds { get; } = [];

        public Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<UserDataSlice>>([]);

        public IReadOnlyDictionary<string, string?> ErasureDeclaration =>
            new Dictionary<string, string?>(StringComparer.Ordinal) { [section] = null };

        public Task EraseForUserAsync(Guid userId, CancellationToken ct)
        {
            order?.Add(section);
            ErasedIds.Add(userId);
            if (Throw is not null) throw Throw;
            return Task.CompletedTask;
        }
    }

    private sealed class ContributorCallLog
    {
        private int _inFlight;

        public int MaxConcurrent { get; private set; }
        public List<string> Order { get; } = [];

        public void Enter(string name)
        {
            Order.Add(name);
            _inFlight++;
            if (_inFlight > MaxConcurrent) MaxConcurrent = _inFlight;
        }

        public void Exit() => _inFlight--;
    }

    /// <summary>
    /// Yields mid-call, so an orchestrator that awaited its contributors concurrently
    /// would leave two of these in flight at once and push MaxConcurrent above 1.
    /// </summary>
    private sealed class ProbeContributor(string name, ContributorCallLog log) : IUserDataContributor
    {
        public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
        {
            log.Enter(name);
            await Task.Yield();
            await Task.Yield();
            log.Exit();
            return [new UserDataSlice(name, new object())];
        }

        public IReadOnlyDictionary<string, string?> ErasureDeclaration =>
            new Dictionary<string, string?>(StringComparer.Ordinal) { [name] = null };

        public Task EraseForUserAsync(Guid userId, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeContributor : IUserDataContributor
    {
        private readonly UserDataSlice[] _slices;
        private readonly Exception? _throw;

        public FakeContributor(string sectionName, object? data)
        {
            _slices = [new UserDataSlice(sectionName, data)];
        }

        public FakeContributor(string sectionName, Exception throwOnCall)
        {
            _slices = [new UserDataSlice(sectionName, null)];
            _throw = throwOnCall;
        }

        public FakeContributor(params UserDataSlice[] slices)
        {
            _slices = slices;
        }

        public Guid? CalledWithUserId { get; private set; }
        public CancellationToken? CalledWithToken { get; private set; }

        public Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
        {
            CalledWithUserId = userId;
            CalledWithToken = ct;
            if (_throw is not null) throw _throw;
            return Task.FromResult<IReadOnlyList<UserDataSlice>>(_slices);
        }

        public IReadOnlyDictionary<string, string?> ErasureDeclaration =>
            _slices.ToDictionary(s => s.SectionName, _ => (string?)null, StringComparer.Ordinal);

        public Guid? ErasedUserId { get; private set; }

        public Task EraseForUserAsync(Guid userId, CancellationToken ct)
        {
            ErasedUserId = userId;
            return Task.CompletedTask;
        }
    }
}
