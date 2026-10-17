using Humans.Preapprovals.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Humans.Preapprovals.Tests.Infrastructure;

/// <summary>Forces EF's one-time <see cref="PreapprovalsDbContext"/> model build before any test runs.</summary>
public sealed class PreapprovalsModelWarmupFixture : IDisposable
{
    private readonly PreapprovalsDbContext _context = new(
        new DbContextOptionsBuilder<PreapprovalsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    public PreapprovalsModelWarmupFixture() => _context.Database.EnsureCreated();

    public void Dispose() => _context.Dispose();
}
