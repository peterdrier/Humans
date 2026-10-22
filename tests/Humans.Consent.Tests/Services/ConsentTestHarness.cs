using Humans.Consent.Data;
using Humans.Base.Enums;
using Humans.Teams.Contracts;
using Humans.Notifications.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace Humans.Consent.Tests.Services;

/// <summary>
/// A deterministic clock, the section context and factory over one in-memory store, and a team
/// registry: <c>LegalDocument.TeamId</c> is a bare cross-section Guid, so a dictionary stands in for teams.
/// </summary>
public abstract class ConsentTestHarness : IDisposable
{
    private protected FakeClock Clock { get; }
    private protected LegalDbContext LegalDb { get; }
    private protected TestDbContextFactory<LegalDbContext> LegalDbFactory { get; }
    private protected INotificationEmitter Notifier { get; } = Substitute.For<INotificationEmitter>();

    /// <summary>Teams by id — the cross-section name lookup the section stitches on.</summary>
    private protected Dictionary<Guid, TeamInfo> Teams { get; } = [];

    protected ConsentTestHarness(Instant? now = null)
    {
        Clock = new FakeClock(now ?? Instant.FromUtc(2026, 3, 1, 12, 0));

        var options = new DbContextOptionsBuilder<LegalDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        LegalDb = new LegalDbContext(options);
        LegalDbFactory = new TestDbContextFactory<LegalDbContext>(options);
    }

    private protected TeamInfo SeedTeam(Guid teamId, string name)
    {
        var team = new TeamInfo(
            teamId,
            name,
            Description: null,
            Slug: name.ToLowerInvariant().Replace(" ", "-", StringComparison.Ordinal),
            IsActive: true,
            IsSystemTeam: false,
            SystemTeamType: SystemTeamType.None,
            RequiresApproval: false,
            IsPublicPage: false,
            IsHidden: false,
            IsPromotedToDirectory: false,
            CreatedAt: Clock.GetCurrentInstant(),
            Members: [])
        {
            UpdatedAt = Clock.GetCurrentInstant()
        };
        Teams[teamId] = team;
        return team;
    }

    private protected Task SaveAllAsync(CancellationToken ct) => LegalDb.SaveChangesAsync(ct);

    public void Dispose()
    {
        LegalDb.Dispose();
        GC.SuppressFinalize(this);
    }
}
