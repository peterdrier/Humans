using Humans.Auth.Data;
using Humans.Auth.Domain;
using AwesomeAssertions;
using Humans.Base.Constants;
using Humans.Integration.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Xunit;
using Humans.Users.Contracts;
using Humans.Users.Data;

namespace Humans.Integration.Tests.AccountMerge;

/// <summary>
/// Reassigns, settles the optional pending email (non-fatal), then tombstones the archived account last. No
/// cross-section transaction: the tombstone is the commit point.
/// </summary>
public class MergeAsyncOrderedTests(HumansTestDatabase database)
    : IntegrationTestBase(database)
{
    [HumansFact(Timeout = 60_000)]
    public async Task MergeAsync_FoldsArchivedIntoSurvivor_AndTombstonesArchivedOnly()
    {
        // The fixture's target survives and its source is archived, as in the other fold tests.
        var runTag = Guid.NewGuid().ToString("N");
        var (sourceId, targetId) = await Factory.SeedMergeFixtureAsync(b =>
        {
            b.WithTargetEmail($"keep-{runTag}@example.com", verified: true, isPrimary: true);   // survivor
            b.WithSourceEmail($"dupe-{runTag}@example.com", verified: true, isPrimary: true);   // archived
            b.WithSourceRoleAssignment($"role-{runTag}");
        });
        var survivorId = targetId;
        var archivedId = sourceId;

        var adminId = await SeedAdminUserAsync();
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var sut = scope.ServiceProvider.GetRequiredService<IAccountMergeService>();
            await sut.MergeAsync(survivorId, archivedId, adminId, ct: TestContext.Current.CancellationToken);
        }

        await using var assertScope = Factory.Services.CreateAsyncScope();
        var db = assertScope.ServiceProvider.GetRequiredService<UsersDbContext>();
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
        var (sourceId, targetId) = await Factory.SeedMergeFixtureAsync(b =>
        {
            b.WithTargetEmail($"keep-{runTag}@example.com", verified: true, isPrimary: true);
            b.WithSourceEmail($"dupe-{runTag}@example.com", verified: true, isPrimary: true);
        });
        var survivorId = targetId;
        var archivedId = sourceId;

        var adminId = await SeedAdminUserAsync();
        await using var scope = Factory.Services.CreateAsyncScope();
        var sut = scope.ServiceProvider.GetRequiredService<IAccountMergeService>();

        // A pending email that is already gone is the desired end state: no throw, and the tombstone still lands.
        var act = async () => await sut.MergeAsync(
            survivorId, archivedId, adminId, pendingEmailIdToVerify: Guid.NewGuid(), ct: TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync();

        await using var assertScope = Factory.Services.CreateAsyncScope();
        var db = assertScope.ServiceProvider.GetRequiredService<UsersDbContext>();
        (await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == archivedId, TestContext.Current.CancellationToken))!.MergedToUserId
            .Should().Be(survivorId, "the tombstone must still be written when the pending email is gone");
    }

    private async Task<Guid> SeedAdminUserAsync()
    {
        // An admin user with an active Admin role: MergeAsync FKs the audit row to it, and the fan-out hits team
        // authorization.
        await using var scope = Factory.Services.CreateAsyncScope();
        var um = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var authDb = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
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

        authDb.RoleAssignments.Add(new RoleAssignment
        {
            Id = Guid.NewGuid(),
            UserId = adminId,
            RoleName = RoleNames.Admin,
            ValidFrom = now.Minus(Duration.FromDays(1)),
            ValidTo = null,
            CreatedAt = now,
            CreatedByUserId = adminId,
        });
        await authDb.SaveChangesAsync(TestContext.Current.CancellationToken);
        return adminId;
    }
}
