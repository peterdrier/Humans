using System.Transactions;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Diagnostics;
using AwesomeAssertions;
using Humans.Base.Enums;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using NodaTime.Testing;

namespace Humans.Camps.Tests.Data;

public sealed class CampRepositoryTests : IDisposable
{
    [HumansTheory]
    [Xunit.InlineData("23505", "IX_camp_role_assignments_unique", true)]
    [Xunit.InlineData("23505", "PK_camp_role_assignments", false)]
    [Xunit.InlineData("23503", "IX_camp_role_assignments_unique", false)]
    public async Task Insert_OnlyTreatsExpectedConstraintAsDuplicate(string sqlState, string constraint, bool duplicate)
    {
        var failure = new DbUpdateException("Save failed", new Npgsql.PostgresException(
            "Constraint violation", "ERROR", "ERROR", sqlState, constraintName: constraint));
        var options = new DbContextOptionsBuilder<CampsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .AddInterceptors(new FailedInsert(failure)).Options;
        var repo = new CampRepository(new TestDbContextFactory<CampsDbContext>(options));
        var row = new CampRoleAssignment { Id = Guid.NewGuid(), CampSeasonId = Guid.NewGuid(), CampMemberId = Guid.NewGuid(), CampRoleDefinitionId = Guid.NewGuid() };
        var insert = () => repo.AddAssignmentAsync(row, Xunit.TestContext.Current.CancellationToken);

        if (duplicate)
            (await insert()).Assigned.Should().BeFalse();
        else
            (await insert.Should().ThrowAsync<DbUpdateException>()).Which.Should().BeSameAs(failure);
    }

    private sealed class FailedInsert(DbUpdateException failure) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default) => throw failure;
    }

    private sealed class MergeTransactionProbe(bool failMemberRemoval) : SaveChangesInterceptor
    {
        public List<string?> TransactionIds { get; } = [];
        public TransactionStatus? CompletedStatus { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var transaction = Transaction.Current;
            if (TransactionIds.Count == 0 && transaction is not null)
                transaction.TransactionCompleted += (_, completed) =>
                    CompletedStatus = completed.Transaction!.TransactionInformation.Status;
            TransactionIds.Add(transaction?.TransactionInformation.LocalIdentifier);
            if (failMemberRemoval && TransactionIds.Count == 2)
                throw new IOException("merged member removal failed");
            return ValueTask.FromResult(result);
        }
    }

    private readonly CampsDbContext _dbContext;
    private readonly FakeClock _clock;
    private readonly CampRepository _repo;

    public CampRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<CampsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _dbContext = new CampsDbContext(options);
        _clock = new FakeClock(Instant.FromUtc(2026, 3, 1, 12, 0));
        _repo = new CampRepository(new TestDbContextFactory<CampsDbContext>(options));
    }

    public void Dispose()
    {
        _dbContext.Dispose();
    }

    [HumansFact]
    public async Task SlugExistsAsync_ReturnsTrue_WhenCampPresent()
    {
        var camp = await SeedCampAsync("test-camp");

        var exists = await _repo.SlugExistsAsync(camp.Slug, Xunit.TestContext.Current.CancellationToken);

        exists.Should().BeTrue();
    }

    [HumansFact]
    public async Task SlugExistsAsync_ReturnsFalse_WhenNoMatch()
    {
        var exists = await _repo.SlugExistsAsync("no-such-camp", Xunit.TestContext.Current.CancellationToken);

        exists.Should().BeFalse();
    }

    [HumansFact]
    public async Task CreateCampAsync_PersistsAllAggregateRows()
    {
        var camp = BuildCamp("new-camp");
        var season = BuildSeason(camp.Id, CampSeasonStatus.Pending, 2026);
        var leadDef = await SeedSpecialRoleDefinitionAsync(CampSpecialRole.Lead);
        var member = new CampMember
        {
            Id = Guid.NewGuid(),
            CampSeasonId = season.Id,
            UserId = Guid.NewGuid(),
            Status = CampMemberStatus.Active,
            RequestedAt = _clock.GetCurrentInstant(),
            ConfirmedAt = _clock.GetCurrentInstant(),
        };
        var assignment = new CampRoleAssignment
        {
            Id = Guid.NewGuid(),
            CampSeasonId = season.Id,
            CampRoleDefinitionId = leadDef.Id,
            CampMemberId = member.Id,
            AssignedAt = _clock.GetCurrentInstant(),
            AssignedByUserId = member.UserId,
        };
        var history = new List<CampHistoricalName>
        {
            new()
            {
                Id = Guid.NewGuid(),
                CampId = camp.Id,
                Name = "Old Name",
                Source = CampNameSource.Manual,
                CreatedAt = _clock.GetCurrentInstant()
            }
        };

        await _repo.CreateCampAsync(camp, season, member, assignment, history, Xunit.TestContext.Current.CancellationToken);

        var persistedCamp = await _dbContext.Camps.AsNoTracking().FirstAsync(c => c.Id == camp.Id, Xunit.TestContext.Current.CancellationToken);
        persistedCamp.Slug.Should().Be("new-camp");
        (await _dbContext.CampSeasons.AsNoTracking().CountAsync(s => s.CampId == camp.Id, Xunit.TestContext.Current.CancellationToken)).Should().Be(1);
        (await _dbContext.CampMembers.AsNoTracking().CountAsync(m => m.CampSeasonId == season.Id, Xunit.TestContext.Current.CancellationToken)).Should().Be(1);
        (await _dbContext.CampRoleAssignments.AsNoTracking().CountAsync(a => a.CampSeasonId == season.Id, Xunit.TestContext.Current.CancellationToken)).Should().Be(1);
        (await _dbContext.CampHistoricalNames.AsNoTracking().CountAsync(h => h.CampId == camp.Id, Xunit.TestContext.Current.CancellationToken)).Should().Be(1);
    }

    [HumansFact]
    public async Task GetActiveLeadUserIdsAsync_ReturnsDistinctActiveLeadUsers()
    {
        // GetActiveLeadUserIdsAsync reads CampRoleAssignment where SpecialRole == Lead.
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        var camp1 = await SeedCampAsync("camp-1");
        var camp2 = await SeedCampAsync("camp-2");

        var leadDef = await SeedSpecialRoleDefinitionAsync(CampSpecialRole.Lead);
        var workshopDef = await SeedSpecialRoleDefinitionAsync(CampSpecialRole.Workshop);
        var regularDef = await SeedRegularRoleDefinitionAsync();
        await SeedLeadAssignmentAsync(camp1.Id, leadDef.Id, userA);
        await SeedLeadAssignmentAsync(camp2.Id, leadDef.Id, userA); // same user, different camp
        await SeedLeadAssignmentAsync(camp2.Id, leadDef.Id, userB);
        // Negative — Workshop and regular role holders are NOT returned.
        await SeedLeadAssignmentAsync(camp1.Id, workshopDef.Id, Guid.NewGuid());
        await SeedLeadAssignmentAsync(camp1.Id, regularDef.Id, Guid.NewGuid());

        var result = await _repo.GetActiveLeadUserIdsAsync(Xunit.TestContext.Current.CancellationToken);

        result.Should().BeEquivalentTo([userA, userB]);
    }

    [HumansFact]
    public async Task SeasonExistsAsync_MatchesTheSeasonsEvent()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var camp = await SeedCampAsync("season-camp");
        var eventId = Guid.NewGuid();
        var season = BuildSeason(camp.Id, CampSeasonStatus.Pending, 2026);
        season.EventSettingsId = eventId;
        _dbContext.CampSeasons.Add(season);
        await _dbContext.SaveChangesAsync(ct);

        (await _repo.SeasonExistsAsync(camp.Id, eventId, unassignedYear: null, ct)).Should().BeTrue();
        (await _repo.SeasonExistsAsync(camp.Id, Guid.NewGuid(), unassignedYear: 2026, ct)).Should().BeFalse();
    }

    [HumansFact]
    public async Task SeasonExistsAsync_CountsAnUnassignedSeasonOnlyForItsYear()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var camp = await SeedCampAsync("other-year");
        await SeedSeasonAsync(camp.Id, 2026, CampSeasonStatus.Active);

        (await _repo.SeasonExistsAsync(camp.Id, Guid.NewGuid(), unassignedYear: 2026, ct)).Should().BeTrue();
        (await _repo.SeasonExistsAsync(camp.Id, Guid.NewGuid(), unassignedYear: 2027, ct)).Should().BeFalse();
        (await _repo.SeasonExistsAsync(camp.Id, Guid.NewGuid(), unassignedYear: null, ct)).Should().BeFalse();
    }

    [HumansFact]
    public async Task HasApprovedSeasonAsync_ReturnsTrue_WhenAnyActiveFullOrWithdrawn()
    {
        var camp = await SeedCampAsync("approved-camp");
        await SeedSeasonAsync(camp.Id, 2025, CampSeasonStatus.Withdrawn);
        await SeedSeasonAsync(camp.Id, 2026, CampSeasonStatus.Pending);

        (await _repo.HasApprovedSeasonAsync(camp.Id, Xunit.TestContext.Current.CancellationToken)).Should().BeTrue();
    }

    [HumansFact]
    public async Task HasApprovedSeasonAsync_ReturnsFalse_WhenAllPendingOrRejected()
    {
        var camp = await SeedCampAsync("never-approved");
        await SeedSeasonAsync(camp.Id, 2025, CampSeasonStatus.Rejected);
        await SeedSeasonAsync(camp.Id, 2026, CampSeasonStatus.Pending);

        (await _repo.HasApprovedSeasonAsync(camp.Id, Xunit.TestContext.Current.CancellationToken)).Should().BeFalse();
    }

    [HumansFact]
    public async Task CountImagesAsync_ReturnsCount()
    {
        var camp = await SeedCampAsync("image-camp");
        _dbContext.CampImages.Add(new CampImage
        {
            Id = Guid.NewGuid(),
            CampId = camp.Id,
            FileName = "a",
            StoragePath = "a",
            ContentType = "image/jpeg",
            SortOrder = 0,
            UploadedAt = _clock.GetCurrentInstant()
        });
        _dbContext.CampImages.Add(new CampImage
        {
            Id = Guid.NewGuid(),
            CampId = camp.Id,
            FileName = "b",
            StoragePath = "b",
            ContentType = "image/jpeg",
            SortOrder = 1,
            UploadedAt = _clock.GetCurrentInstant()
        });
        await _dbContext.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        (await _repo.CountImagesAsync(camp.Id, Xunit.TestContext.Current.CancellationToken)).Should().Be(2);
    }

    [HumansFact]
    public async Task GetSettingsReadOnlyAsync_ReturnsRow()
    {
        _dbContext.CampSettings.Add(new CampSettings
        {
            Id = Guid.NewGuid(),
            PublicYear = 2026,
            OpenSeasons = [2026]
        });
        await _dbContext.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var settings = await _repo.GetSettingsReadOnlyAsync(Xunit.TestContext.Current.CancellationToken);

        settings.Should().NotBeNull();
        settings.PublicYear.Should().Be(2026);
    }

    [HumansFact]
    public async Task SetRegistrationOpenAsync_InsertsThenReportsOnlyRealChanges()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var eventId = Guid.NewGuid();

        (await _repo.SetRegistrationOpenAsync(eventId, true, ct)).Should().BeTrue();
        (await _repo.SetRegistrationOpenAsync(eventId, true, ct)).Should().BeFalse();
        (await _repo.SetRegistrationOpenAsync(eventId, false, ct)).Should().BeTrue();

        (await _repo.GetEventSettingsAsync(ct)).Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new CampEventSettings { EventSettingsId = eventId, RegistrationOpen = false });
    }

    [HumansFact]
    public async Task AddMissingEventSettingsAsync_LeavesExistingRowsAlone()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var closed = Guid.NewGuid();
        var fresh = Guid.NewGuid();
        await _repo.SetRegistrationOpenAsync(closed, false, ct);

        var added = await _repo.AddMissingEventSettingsAsync(
            new Dictionary<Guid, bool> { [closed] = true, [fresh] = true }, ct);

        added.Should().Be(1);
        (await _repo.GetEventSettingsAsync(ct)).Should().BeEquivalentTo(
        [
            new CampEventSettings { EventSettingsId = closed, RegistrationOpen = false },
            new CampEventSettings { EventSettingsId = fresh, RegistrationOpen = true },
        ]);
    }

    [HumansFact]
    public async Task AssignSeasonEventIdsAsync_WritesOnlySeasonsWithoutAnEventId()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var season = await SeedCampWithSeasonAsync("event-id-camp");
        var target = Guid.NewGuid();

        (await _repo.GetSeasonsWithoutEventIdAsync(ct)).Select(s => s.Id).Should().Contain(season.Id);
        (await _repo.AssignSeasonEventIdsAsync(new Dictionary<Guid, Guid> { [season.Id] = target }, ct)).Should().Be(1);
        (await _repo.AssignSeasonEventIdsAsync(new Dictionary<Guid, Guid> { [season.Id] = Guid.NewGuid() }, ct)).Should().Be(0);

        (await _dbContext.CampSeasons.AsNoTracking().SingleAsync(s => s.Id == season.Id, ct)).EventSettingsId.Should().Be(target);
        (await _repo.GetSeasonsWithoutEventIdAsync(ct)).Select(s => s.Id).Should().NotContain(season.Id);
    }

    [HumansFact]
    public async Task ReassignMembershipsToUserAsync_MovesMembership_WhenTargetNotMemberOfSeason()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var season = await SeedCampWithSeasonAsync("merge-camp");
        var source = Guid.NewGuid();
        var target = Guid.NewGuid();
        await SeedMemberAsync(season.Id, source, CampMemberStatus.Active);

        await _repo.ReassignMembershipsToUserAsync(source, target, _clock.GetCurrentInstant(), ct);

        (await _dbContext.CampMembers.AsNoTracking().CountAsync(m => m.UserId == source, ct)).Should().Be(0);
        var moved = await _dbContext.CampMembers.AsNoTracking().SingleAsync(m => m.UserId == target, ct);
        moved.CampSeasonId.Should().Be(season.Id);
        moved.Status.Should().Be(CampMemberStatus.Active);
    }

    [HumansFact]
    public async Task ReassignMembershipsToUserAsync_CarriesRoleAssignments_WhenMembershipMoves()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var season = await SeedCampWithSeasonAsync("role-camp");
        var leadDef = await SeedSpecialRoleDefinitionAsync(CampSpecialRole.Lead);
        var source = Guid.NewGuid();
        var target = Guid.NewGuid();
        var member = await SeedMemberAsync(season.Id, source, CampMemberStatus.Active);
        await SeedAssignmentAsync(season.Id, leadDef.Id, member.Id);

        await _repo.ReassignMembershipsToUserAsync(source, target, _clock.GetCurrentInstant(), ct);

        var assignment = await _dbContext.CampRoleAssignments.AsNoTracking()
            .Include(a => a.CampMember)
            .SingleAsync(a => a.CampSeasonId == season.Id, ct);
        assignment.CampMember.UserId.Should().Be(target);
    }

    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task ReassignMembershipsToUserAsync_CommitsRoleFoldingAndMemberRemovalTogether(bool failMemberRemoval)
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var season = await SeedCampWithSeasonAsync("collide-camp");
        var leadDef = await SeedSpecialRoleDefinitionAsync(CampSpecialRole.Lead);
        var workshopDef = await SeedSpecialRoleDefinitionAsync(CampSpecialRole.Workshop);
        var source = Guid.NewGuid();
        var target = Guid.NewGuid();
        var sourceMember = await SeedMemberAsync(season.Id, source, CampMemberStatus.Active);
        var targetMember = await SeedMemberAsync(season.Id, target, CampMemberStatus.Active);
        // Shared role (lead) must dedup to one; source-only role (workshop) must move across.
        await SeedAssignmentAsync(season.Id, leadDef.Id, sourceMember.Id);
        await SeedAssignmentAsync(season.Id, leadDef.Id, targetMember.Id);
        await SeedAssignmentAsync(season.Id, workshopDef.Id, sourceMember.Id);

        var probe = new MergeTransactionProbe(failMemberRemoval);
        var options = new DbContextOptionsBuilder<CampsDbContext>(
                (DbContextOptions<CampsDbContext>)_dbContext.GetService<IDbContextOptions>())
            .AddInterceptors(probe).Options;
        var repo = new CampRepository(new TestDbContextFactory<CampsDbContext>(options));
        if (failMemberRemoval)
        {
            Func<Task> merge = () => repo.ReassignMembershipsToUserAsync(source, target, _clock.GetCurrentInstant(), ct);
            await merge.Should().ThrowAsync<IOException>();
        }
        else
        {
            await repo.ReassignMembershipsToUserAsync(source, target, _clock.GetCurrentInstant(), ct);
        }

        probe.TransactionIds.Should().HaveCount(2).And.NotContainNulls();
        probe.TransactionIds.Distinct(StringComparer.Ordinal).Should().ContainSingle();
        probe.CompletedStatus.Should().Be(failMemberRemoval ? TransactionStatus.Aborted : TransactionStatus.Committed);
        Transaction.Current.Should().BeNull();
        if (failMemberRemoval) return;

        // Source's duplicate membership is gone; survivor keeps exactly one for the season.
        (await _dbContext.CampMembers.AsNoTracking().CountAsync(m => m.UserId == source, ct)).Should().Be(0);
        (await _dbContext.CampMembers.AsNoTracking()
            .CountAsync(m => m.UserId == target && m.CampSeasonId == season.Id, ct)).Should().Be(1);
        // Survivor's member now holds both roles, lead deduped.
        var targetAssignments = await _dbContext.CampRoleAssignments.AsNoTracking()
            .Where(a => a.CampMemberId == targetMember.Id).ToListAsync(ct);
        targetAssignments.Select(a => a.CampRoleDefinitionId)
            .Should().BeEquivalentTo(new[] { leadDef.Id, workshopDef.Id });
        // Nothing left dangling on the deleted source member.
        (await _dbContext.CampRoleAssignments.AsNoTracking()
            .CountAsync(a => a.CampMemberId == sourceMember.Id, ct)).Should().Be(0);
    }

    [HumansFact]
    public async Task ReassignMembershipsToUserAsync_RepointsRemovedSourceMember_EvenWhenTargetHasLiveMember()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var season = await SeedCampWithSeasonAsync("history-camp");
        var source = Guid.NewGuid();
        var target = Guid.NewGuid();
        await SeedMemberAsync(season.Id, source, CampMemberStatus.Removed);
        await SeedMemberAsync(season.Id, target, CampMemberStatus.Active);

        await _repo.ReassignMembershipsToUserAsync(source, target, _clock.GetCurrentInstant(), ct);

        // Removed rows ignore the active-unique index, so the history row carries over
        // and coexists with the survivor's live membership.
        (await _dbContext.CampMembers.AsNoTracking().CountAsync(m => m.UserId == source, ct)).Should().Be(0);
        var targetMembers = await _dbContext.CampMembers.AsNoTracking()
            .Where(m => m.UserId == target && m.CampSeasonId == season.Id).ToListAsync(ct);
        targetMembers.Select(m => m.Status)
            .Should().BeEquivalentTo(new[] { CampMemberStatus.Active, CampMemberStatus.Removed });
    }

    [HumansFact]
    public async Task ReassignMembershipsToUserAsync_IsIdempotent_WhenRunTwice()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var season = await SeedCampWithSeasonAsync("idem-camp");
        var source = Guid.NewGuid();
        var target = Guid.NewGuid();
        await SeedMemberAsync(season.Id, source, CampMemberStatus.Active);

        using var scope = new TransactionScope(TransactionScopeOption.Required,
            new TransactionOptions { IsolationLevel = IsolationLevel.ReadCommitted },
            TransactionScopeAsyncFlowOption.Enabled);
        await _repo.ReassignMembershipsToUserAsync(source, target, _clock.GetCurrentInstant(), ct);
        // Second run finds nothing left on source — must be a safe no-op, not a duplicate or throw.
        await _repo.ReassignMembershipsToUserAsync(source, target, _clock.GetCurrentInstant(), ct);

        (await _dbContext.CampMembers.AsNoTracking().CountAsync(m => m.UserId == source, ct)).Should().Be(0);
        (await _dbContext.CampMembers.AsNoTracking()
            .CountAsync(m => m.UserId == target && m.CampSeasonId == season.Id, ct)).Should().Be(1);
        Transaction.Current!.TransactionInformation.Status.Should().Be(TransactionStatus.Active);
        scope.Complete();
    }

    [HumansFact]
    public async Task ReassignMembershipsToUserAsync_KeepsActiveStatus_WhenFoldingActiveSourceIntoPendingTarget()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var season = await SeedCampWithSeasonAsync("status-camp");
        var source = Guid.NewGuid();
        var target = Guid.NewGuid();
        await SeedMemberAsync(season.Id, source, CampMemberStatus.Active);
        await SeedMemberAsync(season.Id, target, CampMemberStatus.Pending);

        await _repo.ReassignMembershipsToUserAsync(source, target, _clock.GetCurrentInstant(), ct);

        // Survivor must not be downgraded to the target's Pending state — active
        // role-assignment queries filter Status == Active, so a Pending survivor
        // would silently lose the leads the archived account held.
        var survivor = await _dbContext.CampMembers.AsNoTracking()
            .SingleAsync(m => m.UserId == target && m.CampSeasonId == season.Id, ct);
        survivor.Status.Should().Be(CampMemberStatus.Active);
        (await _dbContext.CampMembers.AsNoTracking().CountAsync(m => m.UserId == source, ct)).Should().Be(0);
    }

    [HumansFact]
    public async Task ReassignMembershipsToUserAsync_PreservesEarlyEntry_WhenFoldingIntoCollidingMember()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var season = await SeedCampWithSeasonAsync("ee-fold-camp");
        var source = Guid.NewGuid();
        var target = Guid.NewGuid();
        var sourceMember = await SeedMemberAsync(season.Id, source, CampMemberStatus.Active);
        sourceMember.HasEarlyEntry = true;
        await _dbContext.SaveChangesAsync(ct);
        await SeedMemberAsync(season.Id, target, CampMemberStatus.Active); // survivor has no EE grant

        await _repo.ReassignMembershipsToUserAsync(source, target, _clock.GetCurrentInstant(), ct);

        // The archived member's early-entry grant must survive the collision-drop.
        var survivor = await _dbContext.CampMembers.AsNoTracking()
            .SingleAsync(m => m.UserId == target && m.CampSeasonId == season.Id, ct);
        survivor.HasEarlyEntry.Should().BeTrue();
    }

    private async Task<CampSeason> SeedCampWithSeasonAsync(string slug)
    {
        var camp = await SeedCampAsync(slug);
        var season = BuildSeason(camp.Id, CampSeasonStatus.Active, 2026);
        _dbContext.CampSeasons.Add(season);
        await _dbContext.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
        return season;
    }

    private async Task<CampMember> SeedMemberAsync(Guid seasonId, Guid userId, CampMemberStatus status)
    {
        var member = new CampMember
        {
            Id = Guid.NewGuid(),
            CampSeasonId = seasonId,
            UserId = userId,
            Status = status,
            RequestedAt = _clock.GetCurrentInstant(),
            ConfirmedAt = status == CampMemberStatus.Active ? _clock.GetCurrentInstant() : null,
        };
        _dbContext.CampMembers.Add(member);
        await _dbContext.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
        return member;
    }

    private async Task SeedAssignmentAsync(Guid seasonId, Guid roleDefinitionId, Guid campMemberId)
    {
        _dbContext.CampRoleAssignments.Add(new CampRoleAssignment
        {
            Id = Guid.NewGuid(),
            CampSeasonId = seasonId,
            CampRoleDefinitionId = roleDefinitionId,
            CampMemberId = campMemberId,
            AssignedAt = _clock.GetCurrentInstant(),
            AssignedByUserId = Guid.NewGuid(),
        });
        await _dbContext.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
    }

    private async Task<Camp> SeedCampAsync(string slug)
    {
        var camp = BuildCamp(slug);
        _dbContext.Camps.Add(camp);
        await _dbContext.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
        return camp;
    }

    private async Task SeedSeasonAsync(Guid campId, int year, CampSeasonStatus status)
    {
        _dbContext.CampSeasons.Add(BuildSeason(campId, status, year));
        await _dbContext.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
    }

    private async Task SeedSettingsAsync()
    {
        _dbContext.CampSettings.Add(new CampSettings
        {
            Id = Guid.NewGuid(),
            PublicYear = 2026,
            OpenSeasons = [2026]
        });
        await _dbContext.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
    }

    private Camp BuildCamp(string slug) => new()
    {
        Id = Guid.NewGuid(),
        Slug = slug,
        ContactEmail = $"{slug}@example.com",
        ContactPhone = "+34600000000",
        CreatedByUserId = Guid.NewGuid(),
        CreatedAt = _clock.GetCurrentInstant(),
        UpdatedAt = _clock.GetCurrentInstant()
    };

    private CampSeason BuildSeason(Guid campId, CampSeasonStatus status, int year) => new()
    {
        Id = Guid.NewGuid(),
        CampId = campId,
        Year = year,
        Name = $"Season-{year}",
        Status = status,
        BlurbLong = "long",
        BlurbShort = "short",
        Languages = "EN",
        AcceptingMembers = YesNoMaybe.Yes,
        KidsWelcome = YesNoMaybe.No,
        KidsVisiting = KidsVisitingPolicy.DaytimeOnly,
        HasPerformanceSpace = PerformanceSpaceStatus.No,
        Vibes = [],
        AdultPlayspace = AdultPlayspacePolicy.No,
        MemberCount = 10,
        CreatedAt = _clock.GetCurrentInstant(),
        UpdatedAt = _clock.GetCurrentInstant()
    };

    private async Task<CampRoleDefinition> SeedSpecialRoleDefinitionAsync(CampSpecialRole specialRole)
    {
        var slug = specialRole.ToString().ToLowerInvariant() + "-test";
        var def = new CampRoleDefinition
        {
            Id = Guid.NewGuid(),
            Name = $"{specialRole} Test Definition",
            Slug = slug,
            SlotCount = 5,
            MinimumRequired = 0,
            SortOrder = 0,
            SpecialRole = specialRole,
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant(),
        };
        _dbContext.CampRoleDefinitions.Add(def);
        await _dbContext.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
        return def;
    }

    private async Task<CampRoleDefinition> SeedRegularRoleDefinitionAsync()
    {
        var def = new CampRoleDefinition
        {
            Id = Guid.NewGuid(),
            Name = $"Regular Role {Guid.NewGuid():N}".Substring(0, 30),
            Slug = $"regular-{Guid.NewGuid():N}".Substring(0, 20),
            SlotCount = 1,
            MinimumRequired = 0,
            SortOrder = 100,
            SpecialRole = CampSpecialRole.None,
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant(),
        };
        _dbContext.CampRoleDefinitions.Add(def);
        await _dbContext.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
        return def;
    }

    private async Task SeedLeadAssignmentAsync(Guid campId, Guid roleDefinitionId, Guid userId)
    {
        // Lands the assignment via the camp's current season and an Active CampMember.
        var season = await _dbContext.CampSeasons.FirstOrDefaultAsync(s => s.CampId == campId, Xunit.TestContext.Current.CancellationToken);
        if (season is null)
        {
            season = BuildSeason(campId, CampSeasonStatus.Active, year: 2026);
            _dbContext.CampSeasons.Add(season);
            await _dbContext.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
        }

        var member = new CampMember
        {
            Id = Guid.NewGuid(),
            CampSeasonId = season.Id,
            UserId = userId,
            Status = CampMemberStatus.Active,
            RequestedAt = _clock.GetCurrentInstant(),
            ConfirmedAt = _clock.GetCurrentInstant(),
        };
        _dbContext.CampMembers.Add(member);

        var assignment = new CampRoleAssignment
        {
            Id = Guid.NewGuid(),
            CampSeasonId = season.Id,
            CampRoleDefinitionId = roleDefinitionId,
            CampMemberId = member.Id,
            AssignedAt = _clock.GetCurrentInstant(),
            AssignedByUserId = userId,
        };
        _dbContext.CampRoleAssignments.Add(assignment);
        await _dbContext.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
    }
}
