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
/// Pins the dual-override pattern on
/// <see cref="RoleAssignmentSaveChangesInterceptor"/>: snapshot the
/// "has-role-assignment-mutation" flag in <c>SavingChangesAsync</c> (before
/// EF flips Added/Modified → Unchanged and Deleted → Detached) and consume
/// it in <c>SavedChangesAsync</c>. Mirrors
/// <see cref="LegalDocumentSaveChangesInterceptorTests"/>.
/// </summary>
public class RoleAssignmentSaveChangesInterceptorTests
{
    [HumansFact]
    public async Task CreatingRoleAssignment_TriggersInvalidateAll()
    {
        var invalidator = new RecordingInvalidator();
        var dbName = Guid.NewGuid().ToString();

        await using var ctx = BuildContext(dbName, invalidator);
        ctx.Set<RoleAssignment>().Add(NewAssignment());
        await ctx.SaveChangesAsync();

        invalidator.InvalidateAllCount.Should().Be(1);
    }

    [HumansFact]
    public async Task UpdatingRoleAssignment_TriggersInvalidateAll()
    {
        var invalidator = new RecordingInvalidator();
        var dbName = Guid.NewGuid().ToString();
        var assignment = NewAssignment();

        await using (var seed = BuildContext(dbName, new RecordingInvalidator()))
        {
            seed.Set<RoleAssignment>().Add(assignment);
            await seed.SaveChangesAsync();
        }

        await using var ctx = BuildContext(dbName, invalidator);
        var loaded = await ctx.Set<RoleAssignment>().FirstAsync(ra => ra.Id == assignment.Id);
        loaded.ValidTo = Instant.FromUtc(2026, 6, 1, 0, 0);
        await ctx.SaveChangesAsync();

        invalidator.InvalidateAllCount.Should().Be(1);
    }

    [HumansFact]
    public async Task DeletingRoleAssignment_TriggersInvalidateAll()
    {
        var invalidator = new RecordingInvalidator();
        var dbName = Guid.NewGuid().ToString();
        var assignment = NewAssignment();

        await using (var seed = BuildContext(dbName, new RecordingInvalidator()))
        {
            seed.Set<RoleAssignment>().Add(assignment);
            await seed.SaveChangesAsync();
        }

        await using var ctx = BuildContext(dbName, invalidator);
        var loaded = await ctx.Set<RoleAssignment>().FirstAsync(ra => ra.Id == assignment.Id);
        ctx.Set<RoleAssignment>().Remove(loaded);
        await ctx.SaveChangesAsync();

        // Post-save the Deleted entry flips to Detached and disappears from
        // ChangeTracker — the SavingChangesAsync snapshot is the only thing
        // that catches this.
        invalidator.InvalidateAllCount.Should().Be(1);
    }

    [HumansFact]
    public async Task SaveWithoutRoleAssignmentEntities_DoesNotInvalidate()
    {
        var invalidator = new RecordingInvalidator();
        await using var ctx = BuildContext(Guid.NewGuid().ToString(), invalidator);

        await ctx.SaveChangesAsync();

        invalidator.InvalidateAllCount.Should().Be(0);
    }

    private static HumansDbContext BuildContext(
        string dbName,
        IRoleAssignmentCacheInvalidator invalidator)
    {
        var services = new ServiceCollection();
        services.AddSingleton(invalidator);
        var provider = services.BuildServiceProvider();

        var interceptor = new RoleAssignmentSaveChangesInterceptor(
            provider,
            NullLogger<RoleAssignmentSaveChangesInterceptor>.Instance);

        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(dbName)
            .AddInterceptors(interceptor)
            .Options;

        return new HumansDbContext(options);
    }

    private static RoleAssignment NewAssignment() => new()
    {
        Id = Guid.NewGuid(),
        UserId = Guid.NewGuid(),
        RoleName = "Board",
        ValidFrom = Instant.FromUtc(2026, 1, 1, 0, 0),
        ValidTo = null,
        CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
        CreatedByUserId = Guid.NewGuid(),
    };

    private sealed class RecordingInvalidator : IRoleAssignmentCacheInvalidator
    {
        public int InvalidateAllCount { get; private set; }
        public void InvalidateAll() => InvalidateAllCount++;
    }
}
