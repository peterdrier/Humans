using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Humans.Testing;

/// <summary>Options for a fresh, private in-memory store.</summary>
public static class TestDbOptions
{
    /// <summary>
    /// A new store per call. Transactions are ignored, not thrown on: the in-memory provider has none, and
    /// code that opens one must still run under test.
    /// </summary>
    public static DbContextOptions<TContext> InMemory<TContext>() where TContext : DbContext =>
        new DbContextOptionsBuilder<TContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
}
