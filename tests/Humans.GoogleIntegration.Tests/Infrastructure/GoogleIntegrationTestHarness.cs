using Humans.AuditLog.Contracts;
using Humans.GoogleIntegration.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace Humans.GoogleIntegration.Tests.Infrastructure;

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

    private protected Task SaveAllAsync(CancellationToken ct = default) =>
        GoogleIntegrationDb.SaveChangesAsync(ct);

    public virtual void Dispose()
    {
        GoogleIntegrationDb.Dispose();
        GC.SuppressFinalize(this);
    }
}
