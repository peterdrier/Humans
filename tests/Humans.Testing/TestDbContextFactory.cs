using Microsoft.EntityFrameworkCore;

namespace Humans.Testing;

/// <summary>
/// Minimal <see cref="IDbContextFactory{TContext}"/> over a shared in-memory context,
/// for per-section contexts (nobodies-collective/Humans#858) — e.g.
/// <c>TestDbContextFactory&lt;StoreDbContext&gt;</c>. Same contract as the production
/// factory: each call returns a fresh context over the same store.
/// </summary>
/// <remarks>
/// Shared via <c>tests/Directory.Build.props</c> rather than owned by one test project,
/// so a section test project created at G5 (nobodies-collective/Humans#866) inherits it
/// along with the rest of the harness.
/// </remarks>
internal sealed class TestDbContextFactory<TContext>(DbContextOptions<TContext> options)
    : IDbContextFactory<TContext>
    where TContext : DbContext
{
    public TContext CreateDbContext() =>
        (TContext)Activator.CreateInstance(typeof(TContext), options)!;

    public Task<TContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(CreateDbContext());
}
