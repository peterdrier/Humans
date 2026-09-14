using AwesomeAssertions;
using Humans.Campaigns.Contracts;
using Humans.Campaigns.Data;
using Humans.Campaigns.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NodaTime;
using NodaTime.Testing;

namespace Humans.Campaigns.Tests.Data;

/// <summary>
/// Repository-level guards for the grant matching and account-merge logic that
/// lives in <see cref="CampaignRepository"/> rather than the service:
/// <c>MarkGrantsRedeemedAsync</c> (ticket-sync redemption matching),
/// <c>ReassignGrantsToUserAsync</c> (merge fold), and the status filtering of
/// the two per-user grant reads.
/// </summary>
public sealed class CampaignRepositoryTests
{
    private readonly FakeClock _clock = new(Instant.FromUtc(2026, 3, 31, 12, 0));

    private readonly TestDbContextFactory<CampaignsDbContext> _factory =
        new(new DbContextOptionsBuilder<CampaignsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private readonly CampaignsDbContext _db;
    private readonly CampaignRepository _repository;

    public CampaignRepositoryTests()
    {
        _db = _factory.CreateDbContext();
        _repository = new CampaignRepository(_factory);
    }

    // ==========================================================================
    // MarkGrantsRedeemedAsync
    // ==========================================================================

    [HumansFact]
    public async Task MarkGrantsRedeemedAsync_MatchesCodesCaseInsensitively()
    {
        var campaign = await SeedCampaignAsync(CampaignStatus.Active);
        var grant = await SeedGrantAsync(campaign, "AbC-123");

        var count = await _repository.MarkGrantsRedeemedAsync(
            [new DiscountCodeRedemption("abc-123", _clock.GetCurrentInstant())],
            Xunit.TestContext.Current.CancellationToken);

        count.Should().Be(1);
        (await ReloadGrantAsync(grant.Id)).RedeemedAt.Should().Be(_clock.GetCurrentInstant());
    }

    [HumansFact]
    public async Task MarkGrantsRedeemedAsync_IgnoresDraftCampaignGrants()
    {
        var campaign = await SeedCampaignAsync(CampaignStatus.Draft);
        var grant = await SeedGrantAsync(campaign, "DRAFT-CODE");

        var count = await _repository.MarkGrantsRedeemedAsync(
            [new DiscountCodeRedemption("DRAFT-CODE", _clock.GetCurrentInstant())],
            Xunit.TestContext.Current.CancellationToken);

        count.Should().Be(0);
        (await ReloadGrantAsync(grant.Id)).RedeemedAt.Should().BeNull();
    }

    [HumansFact]
    public async Task MarkGrantsRedeemedAsync_SkipsAlreadyRedeemedGrants()
    {
        var earlier = Instant.FromUtc(2026, 1, 1, 0, 0);
        var campaign = await SeedCampaignAsync(CampaignStatus.Completed);
        var grant = await SeedGrantAsync(campaign, "USED", redeemedAt: earlier);

        var count = await _repository.MarkGrantsRedeemedAsync(
            [new DiscountCodeRedemption("USED", _clock.GetCurrentInstant())],
            Xunit.TestContext.Current.CancellationToken);

        count.Should().Be(0);
        (await ReloadGrantAsync(grant.Id)).RedeemedAt.Should().Be(earlier);
    }

    [HumansFact]
    public async Task MarkGrantsRedeemedAsync_SameCodeInTwoCampaigns_NewestCampaignWins()
    {
        var older = await SeedCampaignAsync(CampaignStatus.Active, Instant.FromUtc(2025, 1, 1, 0, 0));
        var newer = await SeedCampaignAsync(CampaignStatus.Active, Instant.FromUtc(2026, 1, 1, 0, 0));
        var olderGrant = await SeedGrantAsync(older, "SHARED");
        var newerGrant = await SeedGrantAsync(newer, "SHARED");

        var count = await _repository.MarkGrantsRedeemedAsync(
            [new DiscountCodeRedemption("SHARED", _clock.GetCurrentInstant())],
            Xunit.TestContext.Current.CancellationToken);

        count.Should().Be(1);
        (await ReloadGrantAsync(newerGrant.Id)).RedeemedAt.Should().NotBeNull();
        (await ReloadGrantAsync(olderGrant.Id)).RedeemedAt.Should().BeNull();
    }

    [HumansFact]
    public async Task MarkGrantsRedeemedAsync_NRedemptionsOfSameCode_RedeemNDistinctGrants()
    {
        var first = await SeedCampaignAsync(CampaignStatus.Active);
        var second = await SeedCampaignAsync(CampaignStatus.Active);
        var grantA = await SeedGrantAsync(first, "MULTI");
        var grantB = await SeedGrantAsync(second, "MULTI");

        var count = await _repository.MarkGrantsRedeemedAsync(
            [
                new DiscountCodeRedemption("MULTI", _clock.GetCurrentInstant()),
                new DiscountCodeRedemption("MULTI", _clock.GetCurrentInstant())
            ],
            Xunit.TestContext.Current.CancellationToken);

        count.Should().Be(2);
        (await ReloadGrantAsync(grantA.Id)).RedeemedAt.Should().NotBeNull();
        (await ReloadGrantAsync(grantB.Id)).RedeemedAt.Should().NotBeNull();
    }

    [HumansFact]
    public async Task MarkGrantsRedeemedAsync_BlankCodes_RedeemNothing()
    {
        var campaign = await SeedCampaignAsync(CampaignStatus.Active);
        await SeedGrantAsync(campaign, "REAL");

        var count = await _repository.MarkGrantsRedeemedAsync(
            [new DiscountCodeRedemption("", _clock.GetCurrentInstant())],
            Xunit.TestContext.Current.CancellationToken);

        count.Should().Be(0);
    }

    // ==========================================================================
    // ReassignGrantsToUserAsync
    // ==========================================================================

    [HumansFact]
    public async Task ReassignGrantsToUserAsync_MovesGrantsToTarget()
    {
        var source = Guid.NewGuid();
        var target = Guid.NewGuid();
        var campaign = await SeedCampaignAsync(CampaignStatus.Active);
        var grant = await SeedGrantAsync(campaign, "MOVE", userId: source);

        var count = await _repository.ReassignGrantsToUserAsync(
            source, target, _clock.GetCurrentInstant(), Xunit.TestContext.Current.CancellationToken);

        count.Should().Be(1);
        (await ReloadGrantAsync(grant.Id)).UserId.Should().Be(target);
    }

    [HumansFact]
    public async Task ReassignGrantsToUserAsync_TargetAlreadyGrantedOnCampaign_TargetWinsSourceRowDrops()
    {
        var source = Guid.NewGuid();
        var target = Guid.NewGuid();
        var campaign = await SeedCampaignAsync(CampaignStatus.Active);
        var sourceGrant = await SeedGrantAsync(campaign, "SRC", userId: source);
        var targetGrant = await SeedGrantAsync(campaign, "TGT", userId: target);

        var count = await _repository.ReassignGrantsToUserAsync(
            source, target, _clock.GetCurrentInstant(), Xunit.TestContext.Current.CancellationToken);

        count.Should().Be(1);
        (await _db.CampaignGrants.AsNoTracking()
            .SingleOrDefaultAsync(g => g.Id == sourceGrant.Id, Xunit.TestContext.Current.CancellationToken))
            .Should().BeNull();
        (await ReloadGrantAsync(targetGrant.Id)).UserId.Should().Be(target);
    }

    [HumansFact]
    public async Task ReassignGrantsToUserAsync_SecondSourceRowOnSameCampaign_Drops()
    {
        // Pre-index data could hold two source grants on one campaign; moving
        // both would violate the unique (CampaignId, UserId) index, so the
        // second must drop. The in-memory provider doesn't enforce the index —
        // this pins the dedup logic, not the constraint.
        var source = Guid.NewGuid();
        var target = Guid.NewGuid();
        var campaign = await SeedCampaignAsync(CampaignStatus.Active);
        await SeedGrantAsync(campaign, "DUP-1", userId: source);
        await SeedGrantAsync(campaign, "DUP-2", userId: source);

        var count = await _repository.ReassignGrantsToUserAsync(
            source, target, _clock.GetCurrentInstant(), Xunit.TestContext.Current.CancellationToken);

        count.Should().Be(1);
        (await _db.CampaignGrants.AsNoTracking()
            .CountAsync(g => g.UserId == source, Xunit.TestContext.Current.CancellationToken))
            .Should().Be(0);
    }

    // ==========================================================================
    // Grant reads — campaign-status filtering
    // ==========================================================================

    [HumansFact]
    public async Task GetActiveOrCompletedGrantsForUserAsync_ExcludesDraftCampaignGrants()
    {
        var userId = Guid.NewGuid();
        var draft = await SeedCampaignAsync(CampaignStatus.Draft);
        var active = await SeedCampaignAsync(CampaignStatus.Active);
        var completed = await SeedCampaignAsync(CampaignStatus.Completed);
        await SeedGrantAsync(draft, "D", userId: userId);
        var activeGrant = await SeedGrantAsync(active, "A", userId: userId);
        var completedGrant = await SeedGrantAsync(completed, "C", userId: userId);

        var grants = await _repository.GetActiveOrCompletedGrantsForUserAsync(
            userId, Xunit.TestContext.Current.CancellationToken);

        grants.Select(g => g.Id).Should().BeEquivalentTo([activeGrant.Id, completedGrant.Id]);
    }

    [HumansFact]
    public async Task GetAllGrantsForUserAsync_ReturnsAllStatuses_OnlyForThatUser()
    {
        var userId = Guid.NewGuid();
        var draft = await SeedCampaignAsync(CampaignStatus.Draft);
        var active = await SeedCampaignAsync(CampaignStatus.Active);
        var draftGrant = await SeedGrantAsync(draft, "D", userId: userId);
        var activeGrant = await SeedGrantAsync(active, "A", userId: userId);
        await SeedGrantAsync(active, "OTHER", userId: Guid.NewGuid());

        var grants = await _repository.GetAllGrantsForUserAsync(
            userId, Xunit.TestContext.Current.CancellationToken);

        grants.Select(g => g.Id).Should().BeEquivalentTo([draftGrant.Id, activeGrant.Id]);
    }

    // ==========================================================================
    // Helpers
    // ==========================================================================

    private async Task<Campaign> SeedCampaignAsync(CampaignStatus status, Instant? createdAt = null)
    {
        var campaign = new Campaign
        {
            Id = Guid.NewGuid(),
            Title = "Repo Campaign",
            EmailSubject = "Subject",
            EmailBodyTemplate = "Body",
            Status = status,
            CreatedAt = createdAt ?? _clock.GetCurrentInstant(),
            CreatedByUserId = Guid.NewGuid()
        };
        _db.Campaigns.Add(campaign);
        await _db.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
        _db.ChangeTracker.Clear();
        return campaign;
    }

    private async Task<CampaignGrant> SeedGrantAsync(
        Campaign campaign, string code, Guid? userId = null, Instant? redeemedAt = null)
    {
        var codeRow = new CampaignCode
        {
            Id = Guid.NewGuid(),
            CampaignId = campaign.Id,
            Code = code,
            ImportedAt = _clock.GetCurrentInstant()
        };
        var grant = new CampaignGrant
        {
            Id = Guid.NewGuid(),
            CampaignId = campaign.Id,
            CampaignCodeId = codeRow.Id,
            UserId = userId ?? Guid.NewGuid(),
            AssignedAt = _clock.GetCurrentInstant(),
            RedeemedAt = redeemedAt
        };
        _db.CampaignCodes.Add(codeRow);
        _db.CampaignGrants.Add(grant);
        await _db.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
        _db.ChangeTracker.Clear();
        return grant;
    }

    private async Task<CampaignGrant> ReloadGrantAsync(Guid grantId) =>
        await _db.CampaignGrants.AsNoTracking()
            .SingleAsync(g => g.Id == grantId, Xunit.TestContext.Current.CancellationToken);
}
