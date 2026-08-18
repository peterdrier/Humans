using Microsoft.EntityFrameworkCore;

namespace Humans.Testing;

/// <summary>
/// Minimal <see cref="IDbContextFactory{TContext}"/> over a shared in-memory context,
/// for per-section contexts (nobodies-collective/Humans#858) — e.g.
/// <c>TestDbContextFactory&lt;StoreDbContext&gt;</c>. Same contract as the production
/// factory: each call returns a fresh context over the same store.
/// </summary>
public sealed class TestDbContextFactory<TContext>(DbContextOptions<TContext> options)
    : IDbContextFactory<TContext>
    where TContext : DbContext
{
    public TContext CreateDbContext() =>
        (TContext)Activator.CreateInstance(typeof(TContext), options)!;

    public Task<TContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(CreateDbContext());
}
