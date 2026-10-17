using AwesomeAssertions;
using Humans.Base.Enums;
using Humans.GoogleIntegration.Contracts;
using Humans.GoogleIntegration.Data;
using Humans.GoogleIntegration.Domain;
using Humans.GoogleIntegration.Services;
using Humans.Users.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace Humans.GoogleIntegration.Tests;

/// <summary>The Activity Log read: repository filters, newest-first, cap, and the viewer's name mapping.</summary>
public sealed class GoogleSyncLogActivityLogTests
{
    private static readonly Instant T0 = Instant.FromUtc(2026, 10, 1, 12, 0);
    private readonly DbContextOptions<GoogleIntegrationDbContext> _options =
        new DbContextOptionsBuilder<GoogleIntegrationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;

    private GoogleSyncLogRepository Repo() => new(new SingleContextFactory(_options));

    private async Task SeedAsync(params GoogleSyncLogEntry[] entries)
    {
        await using var ctx = new GoogleIntegrationDbContext(_options);
        await ctx.GoogleSyncLog.AddRangeAsync(entries, Xunit.TestContext.Current.CancellationToken);
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
    }

    private static GoogleSyncLogEntry Entry(
        Instant at, GoogleSyncLogAction action = GoogleSyncLogAction.AccessGranted,
        GoogleSyncSource source = GoogleSyncSource.ScheduledSync, bool success = true, Guid? resourceId = null,
        string description = "Sync: x") => new()
        {
            Id = Guid.NewGuid(),
            Action = action,
            ResourceId = resourceId ?? Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            UserEmail = "member@example.com",
            Role = "reader",
            Source = source,
            Success = success,
            Description = description,
            JobName = "Sync",
            OccurredAt = at
        };

    [HumansFact]
    public async Task GetRecentAsync_NoFilters_ReturnsNewestFirstAcrossResources()
    {
        await SeedAsync(Entry(T0), Entry(T0.Plus(Duration.FromHours(2))), Entry(T0.Plus(Duration.FromHours(1))));

        var rows = await Repo().GetRecentAsync(null, null, null, null, null, null, 10, Xunit.TestContext.Current.CancellationToken);

        rows.Select(r => r.OccurredAt).Should().BeInDescendingOrder().And.HaveCount(3);
    }

    [HumansFact]
    public async Task GetRecentAsync_AppliesLimitToNewestRows()
    {
        await SeedAsync(Enumerable.Range(0, 5).Select(i => Entry(T0.Plus(Duration.FromHours(i)))).ToArray());

        var rows = await Repo().GetRecentAsync(null, null, null, null, null, null, 2, Xunit.TestContext.Current.CancellationToken);

        rows.Select(r => r.OccurredAt).Should().Equal(T0.Plus(Duration.FromHours(4)), T0.Plus(Duration.FromHours(3)));
    }

    [HumansFact]
    public async Task GetRecentAsync_DateRange_IsFromInclusiveToExclusive()
    {
        await SeedAsync(Entry(T0.Minus(Duration.FromSeconds(1))), Entry(T0), Entry(T0.Plus(Duration.FromHours(1))));

        var rows = await Repo().GetRecentAsync(
            T0, T0.Plus(Duration.FromHours(1)), null, null, null, null, 10, Xunit.TestContext.Current.CancellationToken);

        rows.Should().ContainSingle().Which.OccurredAt.Should().Be(T0);
    }

    [HumansFact]
    public async Task GetRecentAsync_ActionSourceAndSuccessFilters_NarrowTogether()
    {
        var match = Entry(T0, GoogleSyncLogAction.AccessRevoked, GoogleSyncSource.ManualSync, success: false);
        await SeedAsync(
            match,
            Entry(T0, GoogleSyncLogAction.AccessGranted, GoogleSyncSource.ManualSync, success: false),
            Entry(T0, GoogleSyncLogAction.AccessRevoked, GoogleSyncSource.ScheduledSync, success: false),
            Entry(T0, GoogleSyncLogAction.AccessRevoked, GoogleSyncSource.ManualSync, success: true));

        var rows = await Repo().GetRecentAsync(
            null, null, GoogleSyncLogAction.AccessRevoked, GoogleSyncSource.ManualSync, false, null, 10,
            Xunit.TestContext.Current.CancellationToken);

        rows.Should().ContainSingle().Which.Id.Should().Be(match.Id);
    }

    [HumansFact]
    public async Task GetRecentAsync_ResourceIds_KeepsOnlyThoseResources()
    {
        var keep = Guid.NewGuid();
        await SeedAsync(Entry(T0, resourceId: keep), Entry(T0), Entry(T0.Plus(Duration.FromHours(1)), resourceId: keep));
        var ct = Xunit.TestContext.Current.CancellationToken;

        var rows = await Repo().GetRecentAsync(null, null, null, null, null, [keep], 10, ct);

        rows.Should().HaveCount(2).And.OnlyContain(r => r.ResourceId == keep);
        (await Repo().GetResourceIdsAsync(ct)).Should().HaveCount(2).And.Contain(keep);
    }

    [HumansFact]
    public async Task Viewer_GetRecentAsync_ResolvesNamesAndFiltersByResourceNameInTheRepository()
    {
        var docs = Guid.NewGuid();
        var other = Guid.NewGuid();
        var repo = Substitute.For<IGoogleSyncLogRepository>();
        repo.GetRecentAsync(default, default, default, default, default, default, default, default).ReturnsForAnyArgs(
            [Entry(T0, resourceId: docs), Entry(T0, resourceId: other), Entry(T0, resourceId: Guid.Empty)]);
        repo.GetResourceIdsAsync(default).ReturnsForAnyArgs([docs, other, Guid.Empty]);
        var resources = Substitute.For<ITeamResourceService>();
        resources.GetResourceNamesByIdsAsync(default!, default).ReturnsForAnyArgs(
            new Dictionary<Guid, string> { [docs] = "Board Docs", [other] = "Camp Group" });
        var sut = new GoogleSyncLogService(repo, resources, Substitute.For<IUserService>(),
            Substitute.For<IUserEmailService>(), Substitute.For<IServiceProvider>(),
            new FakeClock(T0), NullLogger<GoogleSyncLogService>.Instance);
        var ct = Xunit.TestContext.Current.CancellationToken;

        var all = await sut.GetRecentAsync(null, null, null, null, null, null, ct);
        await sut.GetRecentAsync(null, null, null, null, null, " board ", ct);
        await sut.GetRecentAsync(null, null, null, null, null, "nothing", ct);

        all.Select(v => v.ResourceName).Should().Equal("Board Docs", "Camp Group", null);
        all.Should().OnlyContain(v => v.UserId != null);
        await repo.Received(1).GetRecentAsync(null, null, null, null, null, null, 500, ct, null);
        await repo.Received(1).GetRecentAsync(null, null, null, null, null,
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.SequenceEqual(new[] { docs })), 500, ct, "board");
        await repo.ReceivedWithAnyArgs(3).GetRecentAsync(default, default, default, default, default, default, default, default, default);
    }

    [HumansFact]
    public async Task GetRecentAsync_UnlinkedDescriptionFilter_MatchesOnlyGuidEmptyRowsCaseInsensitively()
    {
        var linked = Guid.NewGuid();
        var unlinkedMatch = Entry(T0, resourceId: Guid.Empty,
            description: "GoogleDriveAccessSyncService: Granted x (Shared/Workgroups/Tickets)");
        var unlinkedOther = Entry(T0, resourceId: Guid.Empty,
            description: "GoogleDriveAccessSyncService: Granted x (Shared/Other)");
        var linkedByDescription = Entry(T0, resourceId: linked, description: "Sync: Shared/Workgroups/Tickets");
        var linkedById = Entry(T0, resourceId: Guid.NewGuid());
        await SeedAsync(unlinkedMatch, unlinkedOther, linkedByDescription, linkedById);

        var rows = await Repo().GetRecentAsync(null, null, null, null, null, [linkedById.ResourceId], 10,
            Xunit.TestContext.Current.CancellationToken, "workgroups/TICKETS");

        rows.Select(r => r.Id).Should().BeEquivalentTo([unlinkedMatch.Id, linkedById.Id]);
    }

    [HumansFact]
    public async Task Viewer_GetRecentAsync_NameFilterWithNoMatchingResource_StillQueriesUnlinkedDescriptions()
    {
        var repo = Substitute.For<IGoogleSyncLogRepository>();
        repo.GetResourceIdsAsync(default).ReturnsForAnyArgs([Guid.NewGuid()]);
        var resources = Substitute.For<ITeamResourceService>();
        resources.GetResourceNamesByIdsAsync(default!, default).ReturnsForAnyArgs(new Dictionary<Guid, string>());
        var sut = new GoogleSyncLogService(repo, resources, Substitute.For<IUserService>(),
            Substitute.For<IUserEmailService>(), Substitute.For<IServiceProvider>(),
            new FakeClock(T0), NullLogger<GoogleSyncLogService>.Instance);
        var ct = Xunit.TestContext.Current.CancellationToken;

        await sut.GetRecentAsync(null, null, null, null, null, " Workgroups ", ct);

        await repo.Received(1).GetRecentAsync(null, null, null, null, null,
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 0), 500, ct, "Workgroups");
    }

    private sealed class SingleContextFactory(DbContextOptions<GoogleIntegrationDbContext> options)
        : IDbContextFactory<GoogleIntegrationDbContext>
    {
        public GoogleIntegrationDbContext CreateDbContext() => new(options);

        public Task<GoogleIntegrationDbContext> CreateDbContextAsync(CancellationToken ct = default) =>
            Task.FromResult(new GoogleIntegrationDbContext(options));
    }
}
