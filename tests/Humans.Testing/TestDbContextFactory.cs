using Microsoft.EntityFrameworkCore;

namespace Humans.Testing;

/// <summary>Like the production factory, each call returns a fresh context over the same store.</summary>
public sealed class TestDbContextFactory<TContext>(DbContextOptions<TContext> options)
    : IDbContextFactory<TContext>
    where TContext : DbContext
{
    public TContext CreateDbContext() =>
        (TContext)Activator.CreateInstance(typeof(TContext), options)!;

    public Task<TContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(CreateDbContext());
}
