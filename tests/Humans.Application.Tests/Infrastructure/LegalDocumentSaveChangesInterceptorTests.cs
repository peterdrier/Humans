using AwesomeAssertions;
using Humans.Application.Interfaces.Caching;
using Humans.Domain.Entities;
using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;

namespace Humans.Application.Tests.Infrastructure;

/// <summary>
/// T-04 dual-override invariant. Pins the canonical
/// <c>UserInfoSaveChangesInterceptor</c> pattern on
/// <see cref="LegalDocumentSaveChangesInterceptor"/>: snapshot the
/// "has-legal-mutation" flag in <c>SavingChangesAsync</c> (before EF
/// flips Added/Modified → Unchanged and Deleted → Detached) and consume
/// it in <c>SavedChangesAsync</c>. If a regression flips the interceptor
/// back to inspecting <c>ChangeTracker.Entries()</c> only in
/// <c>SavedChangesAsync</c>, the Update/Delete cases here fail and the
/// global Legal cache silently goes stale until process restart.
/// </summary>
public class LegalDocumentSaveChangesInterceptorTests
{
    [HumansFact]
    public async Task CreatingLegalDocument_TriggersInvalidateAll()
    {
        var invalidator = new RecordingInvalidator();
        var dbName = Guid.NewGuid().ToString();

        await using var ctx = BuildContext(dbName, invalidator);
        ctx.Set<LegalDocument>().Add(NewDocument());
        await ctx.SaveChangesAsync();

        invalidator.InvalidateAllCount.Should().Be(1);
    }

    [HumansFact]
    public async Task UpdatingLegalDocument_TriggersInvalidateAll()
    {
        var invalidator = new RecordingInvalidator();
        var dbName = Guid.NewGuid().ToString();
        var doc = NewDocument();

        await using (var seed = BuildContext(dbName, new RecordingInvalidator()))
        {
            seed.Set<LegalDocument>().Add(doc);
            await seed.SaveChangesAsync();
        }

        await using var ctx = BuildContext(dbName, invalidator);
        var loaded = await ctx.Set<LegalDocument>().FirstAsync(d => d.Id == doc.Id);
        loaded.Name = "Updated Name";
        await ctx.SaveChangesAsync();

        invalidator.InvalidateAllCount.Should().Be(1);
    }

    [HumansFact]
    public async Task DeletingLegalDocument_TriggersInvalidateAll()
    {
        var invalidator = new RecordingInvalidator();
        var dbName = Guid.NewGuid().ToString();
        var doc = NewDocument();

        await using (var seed = BuildContext(dbName, new RecordingInvalidator()))
        {
            seed.Set<LegalDocument>().Add(doc);
            await seed.SaveChangesAsync();
        }

        await using var ctx = BuildContext(dbName, invalidator);
        var loaded = await ctx.Set<LegalDocument>().FirstAsync(d => d.Id == doc.Id);
        ctx.Set<LegalDocument>().Remove(loaded);
        await ctx.SaveChangesAsync();

        // The load-bearing case Codex flagged: post-save the Deleted entry
        // flips to Detached and disappears from ChangeTracker. The
        // SavingChangesAsync snapshot is the only thing that catches this.
        invalidator.InvalidateAllCount.Should().Be(1);
    }

    [HumansFact]
    public async Task SaveWithoutLegalEntities_DoesNotInvalidate()
    {
        var invalidator = new RecordingInvalidator();
        await using var ctx = BuildContext(Guid.NewGuid().ToString(), invalidator);

        // No tracked changes — SaveChanges is a no-op for the interceptor.
        await ctx.SaveChangesAsync();

        invalidator.InvalidateAllCount.Should().Be(0);
    }

    private static HumansDbContext BuildContext(
        string dbName,
        ILegalDocumentCacheInvalidator invalidator)
    {
        var services = new ServiceCollection();
        services.AddSingleton(invalidator);
        var provider = services.BuildServiceProvider();

        var interceptor = new LegalDocumentSaveChangesInterceptor(
            provider,
            NullLogger<LegalDocumentSaveChangesInterceptor>.Instance);

        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(dbName)
            .AddInterceptors(interceptor)
            .Options;

        return new HumansDbContext(options);
    }

    private static LegalDocument NewDocument() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Privacy Policy",
        TeamId = Guid.NewGuid(),
        GitHubFolderPath = "privacy/",
        CurrentCommitSha = "deadbeef",
        IsRequired = true,
        IsActive = true,
        CreatedAt = Instant.FromUtc(2026, 5, 1, 0, 0),
        LastSyncedAt = Instant.FromUtc(2026, 5, 1, 0, 0),
    };

    private sealed class RecordingInvalidator : ILegalDocumentCacheInvalidator
    {
        public int InvalidateAllCount { get; private set; }
        public void InvalidateAll() => InvalidateAllCount++;
    }
}
