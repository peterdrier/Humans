using Microsoft.EntityFrameworkCore;

namespace Humans.Testing;

/// <summary>A section's in-memory context and the factory over the same store.</summary>
public sealed record SectionTestDb<TContext>(TContext Context, TestDbContextFactory<TContext> Factory)
    where TContext : DbContext;

/// <summary>
/// The section stores one test seeds, each a separate in-memory database. A section added with
/// <see cref="Add{TContext}"/> is built on first touch: building one costs an EF service provider, and eager
/// construction pushed a 5s-budget test past its CI timeout.
/// </summary>
public sealed class SectionTestDbs : IDisposable
{
    private readonly List<Func<DbContext?>> _probes = [];

    /// <summary>Declares a section store without building it.</summary>
    public Lazy<SectionTestDb<TContext>> Add<TContext>(Func<DbContextOptions<TContext>, TContext> create)
        where TContext : DbContext
    {
        var lazy = new Lazy<SectionTestDb<TContext>>(() =>
        {
            var options = TestDbOptions.InMemory<TContext>();
            return new SectionTestDb<TContext>(create(options), new TestDbContextFactory<TContext>(options));
        });

        _probes.Add(() => lazy.IsValueCreated ? lazy.Value.Context : null);
        return lazy;
    }

    /// <summary>Includes an already-built context in saves, clears and disposal.</summary>
    public void Track(DbContext context) => _probes.Add(() => context);

    /// <summary>Saves every built context, so a test need not track where each seed landed.</summary>
    public async Task SaveAllAsync(CancellationToken ct = default)
    {
        foreach (var db in Built())
            await db.SaveChangesAsync(ct);
    }

    /// <inheritdoc cref="SaveAllAsync"/>
    public void SaveAll()
    {
        foreach (var db in Built())
            db.SaveChanges();
    }

    /// <summary>
    /// Clears every built context's tracker: clearing only one silently returns stale state for entities another
    /// context owns.
    /// </summary>
    public void ClearTrackers()
    {
        foreach (var db in Built())
            db.ChangeTracker.Clear();
    }

    public void Dispose()
    {
        foreach (var db in Built())
            db.Dispose();
    }

    private IEnumerable<DbContext> Built() => _probes.Select(probe => probe()).OfType<DbContext>();
}
