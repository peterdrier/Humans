using AwesomeAssertions;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Infrastructure.Data;
using Humans.Integration.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Xunit;

namespace Humans.Integration.Tests.AccountMerge;

/// <summary>
/// Integration tests for the ordered <see cref="IAccountMergeService.MergeAsync"/>
/// primitive: fan out <c>IUserMerge.ReassignAsync</c> (archived → survivor), settle
/// the optional pending email (non-fatal), then tombstone the archived account LAST.
/// No cross-section transaction — the tombstone is the commit point and source of truth.
/// </summary>
public class MergeAsyncOrderedTests(HumansWebApplicationFactory factory)
    : IClassFixture<HumansWebApplicationFactory>
{
    [HumansFact(Timeout = 60_000)]
    public async Task MergeAsync_FoldsArchivedIntoSurvivor_AndTombstonesArchivedOnly()
    {
        // The fixture returns (sourceId, targetId). The admin keeps the target
        // ("keep@") and archives the source ("dupe@"), matching the existing
        // source→target fold convention. Addresses are unique per run because the
        // class fixture shares one Postgres DB and the verified-email uniqueness
        // index would otherwise trip across tests.
        var runTag = Guid.NewGuid().ToString("N");
        var (sourceId, targetId) = await factory.SeedMergeFixtureAsync(b =>
        {
            b.WithTargetEmail($"keep-{runTag}@example.com", verified: true, isPrimary: true);   // survivor
            b.WithSourceEmail($"dupe-{runTag}@example.com", verified: true, isPrimary: true);   // archived
            b.WithSourceRoleAssignment($"role-{runTag}");
        });
        var survivorId = targetId;
        var archivedId = sourceId;

        var adminId = await SeedAdminUserAsync();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var sut = scope.ServiceProvider.GetRequiredService<IAccountMergeService>();
            await sut.MergeAsync(survivorId, archivedId, adminId, ct: TestContext.Current.CancellationToken);
        }

        await using var assertScope = factory.Services.CreateAsyncScope();
        var db = assertScope.ServiceProvider.GetRequiredService<HumansDbContext>();
        var survivor = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == survivorId, TestContext.Current.CancellationToken);
        var archived = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == archivedId, TestContext.Current.CancellationToken);
        survivor!.MergedToUserId.Should().BeNull("the survivor is never tombstoned");
        archived!.MergedToUserId.Should().Be(survivorId, "the archived account is tombstoned into the survivor");
        archived.MergedAt.Should().NotBeNull("the tombstone is the merge commit point");
    }

    [HumansFact(Timeout = 60_000)]
    public async Task MergeAsync_WithAlreadyGonePendingEmail_CompletesWithoutThrowing()
    {
        var runTag = Guid.NewGuid().ToString("N");
        var (sourceId, targetId) = await factory.SeedMergeFixtureAsync(b =>
        {
            b.WithTargetEmail($"keep-{runTag}@example.com", verified: true, isPrimary: true);
            b.WithSourceEmail($"dupe-{runTag}@example.com", verified: true, isPrimary: true);
        });
        var survivorId = targetId;
        var archivedId = sourceId;

        var adminId = await SeedAdminUserAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var sut = scope.ServiceProvider.GetRequiredService<IAccountMergeService>();

        // A pending-email id that no longer exists must NOT throw — a gone email is the
        // desired end state — and the archived account MUST still be tombstoned.
        var act = async () => await sut.MergeAsync(
            survivorId, archivedId, adminId, pendingEmailIdToVerify: Guid.NewGuid(), ct: TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync();

        await using var assertScope = factory.Services.CreateAsyncScope();
        var db = assertScope.ServiceProvider.GetRequiredService<HumansDbContext>();
        (await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == archivedId, TestContext.Current.CancellationToken))!.MergedToUserId
            .Should().Be(survivorId, "the tombstone must still be written when the pending email is gone");
    }

    // ==================================================================
    // Helpers
    // ==================================================================

    private async Task<Guid> SeedAdminUserAsync()
    {
        // MergeAsync writes ActorUserId = adminUserId on the audit row (FK'd to
        // AspNetUsers) and the IUserMerge fan-out hits TeamService authorization,
        // which requires an active Admin role. Seed both per call so those FKs
        // resolve and authorization succeeds.
        await using var scope = factory.Services.CreateAsyncScope();
        var um = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var db = scope.ServiceProvider.GetRequiredService<HumansDbContext>();
        var now = SystemClock.Instance.GetCurrentInstant();

        var adminId = Guid.NewGuid();
        var admin = new User
        {
            Id = adminId,
            DisplayName = "Test Admin",
            Email = $"admin-{adminId:N}@test.local",
            UserName = $"admin-{adminId:N}@test.local",
            CreatedAt = now,
        };
        var result = await um.CreateAsync(admin);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                "Failed to seed admin user for MergeAsyncOrderedTests: "
                + string.Join("; ", result.Errors.Select(e => e.Description)));
        }

        db.RoleAssignments.Add(new RoleAssignment
        {
            Id = Guid.NewGuid(),
            UserId = adminId,
            RoleName = RoleNames.Admin,
            ValidFrom = now.Minus(Duration.FromDays(1)),
            ValidTo = null,
            CreatedAt = now,
            CreatedByUserId = adminId,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return adminId;
    }
}
