using Microsoft.EntityFrameworkCore;
using Humans.Users.Data;

namespace Humans.Users.Tests.Infrastructure;

/// <summary>
/// Each <see cref="CreateDbContextAsync"/> returns a fresh context over the same in-memory store,
/// as the production factory does.
/// </summary>
internal sealed class TestDbContextFactory(DbContextOptions<UsersDbContext> options)
    : IDbContextFactory<UsersDbContext>
{
    public UsersDbContext CreateDbContext() => new(options);

    public Task<UsersDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(CreateDbContext());
}
