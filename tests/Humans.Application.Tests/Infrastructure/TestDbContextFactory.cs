using Microsoft.EntityFrameworkCore;
using Humans.Infrastructure.Data;

namespace Humans.Application.Tests.Infrastructure;

/// <summary>
/// Minimal <see cref="IDbContextFactory{TContext}"/> backed by a shared
/// in-memory <see cref="HumansDbContext"/> for unit tests.
/// <para>
/// Each <see cref="CreateDbContextAsync"/> returns a fresh <see cref="HumansDbContext"/>
/// instance connected to the same in-memory store, matching the production
/// IDbContextFactory behavior while keeping the shared data visible across calls.
/// </para>
/// </summary>
internal sealed class TestDbContextFactory(DbContextOptions<HumansDbContext> options)
    : IDbContextFactory<HumansDbContext>
{
    public HumansDbContext CreateDbContext() => new(options);

    public Task<HumansDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(CreateDbContext());
}

/// <summary>
/// Generic variant for per-section contexts (nobodies-collective/Humans#858),
/// e.g. <c>TestDbContextFactory&lt;SystemSettingsDbContext&gt;</c>. Same contract as
/// the non-generic factory above.
/// </summary>
internal sealed class TestDbContextFactory<TContext>(DbContextOptions<TContext> options)
    : IDbContextFactory<TContext>
    where TContext : DbContext
{
    public TContext CreateDbContext() =>
        (TContext)Activator.CreateInstance(typeof(TContext), options)!;

    public Task<TContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(CreateDbContext());
}
