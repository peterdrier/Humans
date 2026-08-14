using Microsoft.EntityFrameworkCore;
using Humans.Infrastructure.Data;
using Humans.Users.Data;

namespace Humans.Users.Tests.Infrastructure;

/// <summary>
/// Minimal <see cref="IDbContextFactory{TContext}"/> backed by a shared
/// in-memory <see cref="UsersDbContext"/> for unit tests.
/// <para>
/// Each <see cref="CreateDbContextAsync"/> returns a fresh <see cref="UsersDbContext"/>
/// instance connected to the same in-memory store, matching the production
/// IDbContextFactory behavior while keeping the shared data visible across calls.
/// </para>
/// </summary>
internal sealed class TestDbContextFactory(DbContextOptions<UsersDbContext> options)
    : IDbContextFactory<UsersDbContext>
{
    public UsersDbContext CreateDbContext() => new(options);

    public Task<UsersDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(CreateDbContext());
}
