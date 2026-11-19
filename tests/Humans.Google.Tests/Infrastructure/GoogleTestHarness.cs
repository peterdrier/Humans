using Humans.AuditLog.Contracts;
using Humans.Google.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace Humans.Google.Tests.Infrastructure;

public abstract class GoogleTestHarness : IDisposable
{
    private protected GoogleIntegrationDbContext GoogleDb { get; }

    private protected TestDbContextFactory<GoogleIntegrationDbContext> GoogleDbFactory { get; }

    private protected FakeClock Clock { get; }

    private protected IAuditLogService AuditLog { get; } = Substitute.For<IAuditLogService>();

    protected GoogleTestHarness(Instant? now = null)
    {
        var options = new DbContextOptionsBuilder<GoogleIntegrationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        GoogleDb = new GoogleIntegrationDbContext(options);
        GoogleDbFactory = new TestDbContextFactory<GoogleIntegrationDbContext>(options);
        Clock = new FakeClock(now ?? Instant.FromUtc(2026, 3, 1, 12, 0));
    }

    private protected Task SaveAllAsync(CancellationToken ct = default) =>
        GoogleDb.SaveChangesAsync(ct);

    public virtual void Dispose()
    {
        GoogleDb.Dispose();
        GC.SuppressFinalize(this);
    }
}
