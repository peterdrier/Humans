using AwesomeAssertions;
using Humans.Base.Demo;
using Humans.Development.Data;
using Humans.Development.Domain;
using Humans.Development.Services.Demo;
using NodaTime;
using NodaTime.Testing;

namespace Humans.Development.Tests;

public class DemoWorldTests
{
    private static readonly FakeClock Clock = new(Instant.FromUtc(2026, 11, 19, 12, 0));

    [HumansFact]
    public void Order_puts_a_story_after_what_it_waits_for_and_refuses_gaps_and_cycles()
    {
        var cast = new Story("cast");
        var teams = new Story("teams", "cast");
        var shifts = new Story("shifts", "teams");

        DemoWorldRunner.Order([shifts, cast, teams]).Select(s => s.Key.Value)
            .Should().Equal("cast", "teams", "shifts");

        var gap = () => DemoWorldRunner.Order([shifts, cast]);
        gap.Should().Throw<InvalidOperationException>().WithMessage("*'shifts' runs after 'teams'*");

        var cycle = () => DemoWorldRunner.Order([new Story("a", "b"), new Story("b", "a")]);
        cycle.Should().Throw<InvalidOperationException>().WithMessage("*wait for each other*");
    }

    [HumansFact]
    public async Task Ensure_creates_once_repairs_a_vanished_row_and_adopts_a_pending_one()
    {
        var repository = new MemoryLedger();
        var rows = new HashSet<Guid>();
        var creates = 0;
        Task<bool> ExistsAsync(Guid id, CancellationToken _) => Task.FromResult(rows.Contains(id));
        Task<Guid> CreateAsync(Guid planned, CancellationToken _)
        {
            creates++;
            rows.Add(planned);
            return Task.FromResult(planned);
        }

        var first = await World(repository).EnsureAsync("k", ExistsAsync, CreateAsync, default);
        var again = await World(repository).EnsureAsync("k", ExistsAsync, CreateAsync, default);
        again.Should().Be(first);
        creates.Should().Be(1, "a second run adds nothing");

        rows.Remove(first);
        var repaired = await World(repository).EnsureAsync("k", ExistsAsync, CreateAsync, default);
        repaired.Should().NotBe(first, "a vanished row is made again under a fresh id");
        creates.Should().Be(2);

        // A run cut off mid-create: the row may be half-built, so create runs again on the same id to finish it.
        var entry = repository.Entries.Single();
        entry.State = DemoLedgerState.Pending;
        var finished = await World(repository).EnsureAsync("k", ExistsAsync, CreateAsync, default);
        finished.Should().Be(repaired, "the pending row keeps its planned id");
        creates.Should().Be(3);
        repository.Entries.Single().State.Should().Be(DemoLedgerState.Made);
    }

    private static DemoWorld World(MemoryLedger repository) =>
        new(1, Guid.NewGuid(), [.. repository.Entries], repository, Clock, []);

    private sealed class Story(string key, params string[] after) : IDemoStory
    {
        public DemoStoryKey Key { get; } = new(key);
        public IReadOnlyList<DemoStoryKey> After { get; } = [.. after.Select(a => new DemoStoryKey(a))];
        public Task TellAsync(IDemoWorld world, CancellationToken ct) => Task.CompletedTask;
        public Task RetireAsync(IDemoWorld world, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class MemoryLedger : IDemoLedgerRepository
    {
        public List<DemoLedgerEntry> Entries { get; } = [];

        public Task<IReadOnlyList<DemoLedgerEntry>> GetAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DemoLedgerEntry>>([.. Entries]);

        public Task SaveAsync(DemoLedgerEntry entry, CancellationToken ct = default)
        {
            Entries.RemoveAll(e => e.Id == entry.Id);
            Entries.Add(entry);
            return Task.CompletedTask;
        }
    }
}
