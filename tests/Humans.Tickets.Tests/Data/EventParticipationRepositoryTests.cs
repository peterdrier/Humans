using AwesomeAssertions;
using Humans.Tickets.Contracts;
using Humans.Tickets.Data;
using Humans.Tickets.Domain;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Humans.Tickets.Tests.Data;

/// <summary>
/// <c>tickets_event_participations</c>: the lifecycle rules moved from Users' repository in
/// decomposition commit 13, plus the legacy seed and cleared-row behaviour the move added.
/// </summary>
public sealed class EventParticipationRepositoryTests : IDisposable
{
    private static readonly Instant Now = Instant.FromUtc(2026, 7, 8, 12, 0);
    private readonly TicketsDbContext _db;
    private readonly EventParticipationRepository _repo;

    public EventParticipationRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<TicketsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new TicketsDbContext(options);
        _repo = new EventParticipationRepository(new TestDbContextFactory<TicketsDbContext>(options));
    }

    public void Dispose() => _db.Dispose();

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private static EventParticipation Row(
        Guid userId, ParticipationStatus? status, ParticipationSource source,
        int year = 2026, Instant? checkedInAt = null, Instant? declaredAt = null) => new()
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Year = year,
            Status = status,
            Source = source,
            CheckedInAt = checkedInAt,
            DeclaredAt = declaredAt,
        };

    private async Task SeedAsync(params EventParticipation[] rows)
    {
        await _db.EventParticipations.AddRangeAsync(rows, Ct);
        await _db.SaveChangesAsync(Ct);
        _db.ChangeTracker.Clear();
    }

    private Task<List<EventParticipation>> RowsAsync(Guid userId) =>
        _db.EventParticipations.AsNoTracking().Where(r => r.UserId == userId).ToListAsync(Ct);

    // ─── UpsertAsync ─────────────────────────────────────────────────────────

    [HumansFact]
    public async Task Upsert_CreatesNewRecord_WhenNoneExists()
    {
        var userId = Guid.NewGuid();

        (await _repo.UpsertAsync(userId, 2026, ParticipationStatus.NotAttending, ParticipationSource.UserDeclared,
            Now, null, legacy: null, Ct)).Should().BeTrue();

        var row = (await RowsAsync(userId)).Should().ContainSingle().Subject;
        row.Status.Should().Be(ParticipationStatus.NotAttending);
        row.Source.Should().Be(ParticipationSource.UserDeclared);
        row.DeclaredAt.Should().Be(Now);
    }

    [HumansFact]
    public async Task Upsert_OverwritesNonAttendedRecord()
    {
        var userId = Guid.NewGuid();
        await SeedAsync(Row(userId, ParticipationStatus.NotAttending, ParticipationSource.UserDeclared, declaredAt: Now));

        await _repo.UpsertAsync(userId, 2026, ParticipationStatus.Ticketed, ParticipationSource.TicketSync,
            null, null, legacy: null, Ct);

        var row = (await RowsAsync(userId)).Single();
        row.Status.Should().Be(ParticipationStatus.Ticketed);
        row.Source.Should().Be(ParticipationSource.TicketSync);
        row.DeclaredAt.Should().BeNull();
    }

    [HumansFact]
    public async Task Upsert_RefusesToDowngradeAttended()
    {
        var userId = Guid.NewGuid();
        await SeedAsync(Row(userId, ParticipationStatus.Attended, ParticipationSource.TicketSync, checkedInAt: Now));

        (await _repo.UpsertAsync(userId, 2026, ParticipationStatus.NotAttending, ParticipationSource.UserDeclared,
            Now, null, legacy: null, Ct)).Should().BeFalse();

        (await RowsAsync(userId)).Single().Status.Should().Be(ParticipationStatus.Attended);
    }

    [HumansFact]
    public async Task Upsert_FillsMissingArrival_OnAttendedRecord_AndNeverOverwritesIt()
    {
        var userId = Guid.NewGuid();
        await SeedAsync(Row(userId, ParticipationStatus.Attended, ParticipationSource.AdminBackfill));

        (await _repo.UpsertAsync(userId, 2026, ParticipationStatus.Attended, ParticipationSource.TicketSync,
            null, Now, legacy: null, Ct)).Should().BeTrue();
        (await _repo.UpsertAsync(userId, 2026, ParticipationStatus.Attended, ParticipationSource.TicketSync,
            null, Now + Duration.FromHours(1), legacy: null, Ct)).Should().BeFalse();

        var row = (await RowsAsync(userId)).Single();
        row.CheckedInAt.Should().Be(Now);
        row.Source.Should().Be(ParticipationSource.AdminBackfill);
    }

    [HumansFact]
    public async Task Upsert_TicketedToAttended_FillsArrival()
    {
        var userId = Guid.NewGuid();
        await SeedAsync(Row(userId, ParticipationStatus.Ticketed, ParticipationSource.TicketSync));

        await _repo.UpsertAsync(userId, 2026, ParticipationStatus.Attended, ParticipationSource.TicketSync,
            null, Now, legacy: null, Ct);

        var row = (await RowsAsync(userId)).Single();
        row.Status.Should().Be(ParticipationStatus.Attended);
        row.CheckedInAt.Should().Be(Now);
    }

    [HumansFact]
    public async Task Upsert_StartsFromTheLegacyRow_WhenTheTableHasNone()
    {
        var userId = Guid.NewGuid();
        var legacy = Row(userId, ParticipationStatus.Attended, ParticipationSource.TicketSync, checkedInAt: Now);

        // A legacy Attended row is permanent: the declaration is refused and nothing is written.
        (await _repo.UpsertAsync(userId, 2026, ParticipationStatus.NotAttending, ParticipationSource.UserDeclared,
            Now, null, legacy, Ct)).Should().BeFalse();
        (await RowsAsync(userId)).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Upsert_OverwritesAClearedRow()
    {
        var userId = Guid.NewGuid();
        await SeedAsync(Row(userId, null, ParticipationSource.UserDeclared));

        await _repo.UpsertAsync(userId, 2026, ParticipationStatus.Ticketed, ParticipationSource.TicketSync,
            null, null, legacy: null, Ct);

        (await RowsAsync(userId)).Single().Status.Should().Be(ParticipationStatus.Ticketed);
    }

    // ─── ClearAsync ──────────────────────────────────────────────────────────

    [HumansFact]
    public async Task Clear_DeletesWhenSourceMatches_AndNoLegacyRow()
    {
        var userId = Guid.NewGuid();
        await SeedAsync(Row(userId, ParticipationStatus.Ticketed, ParticipationSource.TicketSync));

        (await _repo.ClearAsync(userId, 2026, ParticipationSource.TicketSync, legacy: null, Ct)).Should().BeTrue();

        (await RowsAsync(userId)).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Clear_KeepsAClearedRow_WhenALegacyRowBacksTheYear()
    {
        var userId = Guid.NewGuid();
        await SeedAsync(Row(userId, ParticipationStatus.NotAttending, ParticipationSource.UserDeclared, declaredAt: Now));
        var legacy = Row(userId, ParticipationStatus.NotAttending, ParticipationSource.UserDeclared, declaredAt: Now);

        (await _repo.ClearAsync(userId, 2026, ParticipationSource.UserDeclared, legacy, Ct)).Should().BeTrue();

        var row = (await RowsAsync(userId)).Single();
        row.Status.Should().BeNull();
        row.DeclaredAt.Should().BeNull();
    }

    [HumansFact]
    public async Task Clear_OfALegacyOnlyRecord_WritesAClearedRow()
    {
        var userId = Guid.NewGuid();
        var legacy = Row(userId, ParticipationStatus.Ticketed, ParticipationSource.TicketSync);

        (await _repo.ClearAsync(userId, 2026, ParticipationSource.TicketSync, legacy, Ct)).Should().BeTrue();

        (await RowsAsync(userId)).Single().Status.Should().BeNull();
    }

    [HumansTheory]
    [Xunit.InlineData(ParticipationStatus.NotAttending, ParticipationSource.UserDeclared)]
    [Xunit.InlineData(ParticipationStatus.Attended, ParticipationSource.TicketSync)]
    public async Task Clear_RefusesOtherSources_AndAttended(ParticipationStatus status, ParticipationSource source)
    {
        var userId = Guid.NewGuid();
        await SeedAsync(Row(userId, status, source));

        (await _repo.ClearAsync(userId, 2026, ParticipationSource.TicketSync, legacy: null, Ct)).Should().BeFalse();

        (await RowsAsync(userId)).Single().Status.Should().Be(status);
    }

    // ─── BackfillAsync ───────────────────────────────────────────────────────

    [HumansFact]
    public async Task Backfill_AddsAndUpdates_PreservesAttended_AndReportsOnlyChanges()
    {
        var newId = Guid.NewGuid();
        var existingId = Guid.NewGuid();
        var attendedId = Guid.NewGuid();
        var legacyId = Guid.NewGuid();
        await SeedAsync(
            Row(existingId, ParticipationStatus.NotAttending, ParticipationSource.UserDeclared, year: 2025),
            Row(attendedId, ParticipationStatus.Attended, ParticipationSource.TicketSync, year: 2025));
        var legacyAttended = Row(legacyId, ParticipationStatus.Attended, ParticipationSource.TicketSync, year: 2025);
        var entries = new List<(Guid UserId, ParticipationStatus Status)>
        {
            (newId, ParticipationStatus.Ticketed),
            (existingId, ParticipationStatus.Ticketed),
            (attendedId, ParticipationStatus.NotAttending),
            (legacyId, ParticipationStatus.NotAttending),
        };
        var legacy = new Dictionary<Guid, EventParticipation> { [legacyId] = legacyAttended };

        (await _repo.BackfillAsync(2025, entries, legacy, Ct)).Should().Equal(
            (newId, ParticipationStatus.Ticketed), (existingId, ParticipationStatus.Ticketed));
        (await _repo.BackfillAsync(2025, entries, legacy, Ct)).Should().BeEmpty();

        (await RowsAsync(newId)).Single().Source.Should().Be(ParticipationSource.AdminBackfill);
        (await RowsAsync(existingId)).Single().Status.Should().Be(ParticipationStatus.Ticketed);
        (await RowsAsync(attendedId)).Single().Status.Should().Be(ParticipationStatus.Attended);
        (await RowsAsync(legacyId)).Single().Status.Should().Be(ParticipationStatus.Attended);
    }

    // ─── ReassignAsync (merge fold) ──────────────────────────────────────────

    [HumansTheory]
    [Xunit.InlineData(ParticipationStatus.Ticketed, false)]
    [Xunit.InlineData(ParticipationStatus.Attended, false)]
    [Xunit.InlineData(ParticipationStatus.Attended, true)]
    public async Task Reassign_HigherPrecedenceWins_AndKnownArrivalIsKept(
        ParticipationStatus targetStatus, bool targetHasArrival)
    {
        var sourceId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var targetArrival = targetHasArrival ? Now + Duration.FromHours(1) : (Instant?)null;
        await SeedAsync(
            Row(sourceId, ParticipationStatus.Attended, ParticipationSource.TicketSync, checkedInAt: Now),
            Row(targetId, targetStatus, ParticipationSource.AdminBackfill, checkedInAt: targetArrival));

        await _repo.ReassignAsync(sourceId, targetId, [], [], Ct);

        (await RowsAsync(sourceId)).Should().BeEmpty();
        var row = (await RowsAsync(targetId)).Single();
        row.Status.Should().Be(ParticipationStatus.Attended);
        row.Source.Should().Be(targetStatus == ParticipationStatus.Ticketed
            ? ParticipationSource.TicketSync : ParticipationSource.AdminBackfill);
        row.CheckedInAt.Should().Be(targetArrival ?? Now);
    }

    [HumansFact]
    public async Task Reassign_MovesSourceOnlyYears_AndCountsLegacyRowsOnBothSides()
    {
        var sourceId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        await SeedAsync(Row(sourceId, ParticipationStatus.NotAttending, ParticipationSource.UserDeclared, year: 2024));
        var legacySource = new[] { Row(sourceId, ParticipationStatus.Ticketed, ParticipationSource.TicketSync, year: 2025) };
        var legacyTarget = new[] { Row(targetId, ParticipationStatus.Attended, ParticipationSource.TicketSync, year: 2025, checkedInAt: Now) };

        await _repo.ReassignAsync(sourceId, targetId, legacySource, legacyTarget, Ct);

        var target = await RowsAsync(targetId);
        target.Single(r => r.Year == 2024).Status.Should().Be(ParticipationStatus.NotAttending);
        target.Single(r => r.Year == 2025).Status.Should().Be(ParticipationStatus.Attended);
        // The 2024 row had no legacy backing: deleted. 2025 was legacy-backed: cleared, so the
        // frozen Users row stays shadowed.
        var source = await RowsAsync(sourceId);
        source.Should().ContainSingle().Which.Should().Match<EventParticipation>(r => r.Year == 2025 && r.Status == null);
    }

    // ─── TryInsertAsync (copy) ───────────────────────────────────────────────

    [HumansFact]
    public async Task TryInsert_SkipsAYearThatHasAnyRow_ClearedIncluded()
    {
        var userId = Guid.NewGuid();
        await SeedAsync(Row(userId, null, ParticipationSource.UserDeclared));

        (await _repo.TryInsertAsync(Row(userId, ParticipationStatus.NotAttending, ParticipationSource.UserDeclared), Ct))
            .Should().BeFalse();
        (await _repo.TryInsertAsync(Row(userId, ParticipationStatus.Ticketed, ParticipationSource.TicketSync, year: 2025), Ct))
            .Should().BeTrue();
    }
}
