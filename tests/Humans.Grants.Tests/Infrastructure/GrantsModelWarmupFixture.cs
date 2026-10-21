using Humans.Grants.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Humans.Grants.Tests.Infrastructure;

/// <summary>Forces EF's one-time <see cref="GrantsDbContext"/> model build before any test runs.</summary>
public sealed class GrantsModelWarmupFixture : IDisposable
{
    private readonly GrantsDbContext _context = new(
        new DbContextOptionsBuilder<GrantsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    public GrantsModelWarmupFixture() => _context.Database.EnsureCreated();

    public void Dispose() => _context.Dispose();
}
