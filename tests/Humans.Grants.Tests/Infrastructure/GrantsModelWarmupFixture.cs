using Humans.Grants.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Humans.Grants.Tests.Infrastructure;

/// <summary>Forces EF's one-time <see cref="GrantsDbContext"/> model build before any test runs.</summary>
public sealed class GrantsModelWarmupFixture : IDisposable
{
    private readonly GrantsDbContext _context = new(
        TestDbOptions.InMemory<GrantsDbContext>());

    public GrantsModelWarmupFixture() => _context.Database.EnsureCreated();

    public void Dispose() => _context.Dispose();
}
