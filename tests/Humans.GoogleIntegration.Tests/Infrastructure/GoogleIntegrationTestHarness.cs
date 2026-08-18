using Humans.AuditLog.Contracts;
using Humans.GoogleIntegration.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace Humans.GoogleIntegration.Tests.Infrastructure;

/// <summary>
/// The five members the section's two harness-derived test classes actually used out of
/// <c>Humans.Application.Tests</c>' 315-line <c>ServiceTestHarness</c>: the section context
/// and its factory, a deterministic clock, an audit substitute and a flush.
/// </summary>
/// <remarks>
/// Inlined rather than shared (G5-SECTION-TEMPLATE.md step 8): the base class is built around
/// an in-memory <c>UsersDbContext</c> and four other sections' contexts, none of which these
/// tests touch. The <c>SeedTeam</c> calls that came with them staged rows nothing read —
/// <c>ITeamService</c> is substituted in both files and every assertion reads
/// <c>google_resources</c> — so they went with the base class rather than dragging a
/// <c>Humans.Teams</c> reference into this project for a fixture with no reader.
/// </remarks>
public abstract class GoogleIntegrationTestHarness : IDisposable
{
    private protected GoogleIntegrationDbContext GoogleIntegrationDb { get; }

    private protected TestDbContextFactory<GoogleIntegrationDbContext> GoogleIntegrationDbFactory { get; }

    private protected FakeClock Clock { get; }

    private protected IAuditLogService AuditLog { get; } = Substitute.For<IAuditLogService>();

    protected GoogleIntegrationTestHarness(Instant? now = null)
    {
        var options = new DbContextOptionsBuilder<GoogleIntegrationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        GoogleIntegrationDb = new GoogleIntegrationDbContext(options);
        GoogleIntegrationDbFactory = new TestDbContextFactory<GoogleIntegrationDbContext>(options);
        Clock = new FakeClock(now ?? Instant.FromUtc(2026, 3, 1, 12, 0));
    }

    /// <summary>
    /// Named for the base class it replaces, so the ~11 staged-seed call sites in the two
    /// moved files are unchanged. Only one context to flush here.
    /// </summary>
    private protected Task SaveAllAsync(CancellationToken ct = default) =>
        GoogleIntegrationDb.SaveChangesAsync(ct);

    public virtual void Dispose()
    {
        GoogleIntegrationDb.Dispose();
        GC.SuppressFinalize(this);
    }
}
