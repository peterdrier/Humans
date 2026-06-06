using AwesomeAssertions;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Integration.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Xunit;

namespace Humans.Integration.Tests.AccountMerge;

/// <summary>
/// Proves the engine self-reconciles merge requests: merging a pair through
/// <see cref="IAccountMergeService.MergeAsync"/> closes any pending request for
/// that pair (via the internal CloseRequestsForPairAsync), so the unified merge
/// flow never leaves orphaned Pending rows behind.
/// </summary>
public class ReconcileOrphanTests(HumansWebApplicationFactory factory)
    : IClassFixture<HumansWebApplicationFactory>
{
    [HumansFact(Timeout = 60_000)]
    public async Task MergeAsync_ClosesPendingRequestForThePair()
    {
        var adminId = await SeedAdminUserAsync();
        var (requestId, targetId, sourceId) =
            await factory.SeedPendingMergeRequestAsync($"shared-{Guid.NewGuid():N}@example.com");

        // Merge the pair through the engine (survivor = target, archived = source).
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var sut = scope.ServiceProvider.GetRequiredService<IAccountMergeService>();
            await sut.MergeAsync(targetId, sourceId, adminId);
        }

        await using var assertScope = factory.Services.CreateAsyncScope();
        var db = assertScope.ServiceProvider.GetRequiredService<HumansDbContext>();
        (await db.AccountMergeRequests.AsNoTracking().FirstOrDefaultAsync(r => r.Id == requestId))!.Status
            .Should().Be(AccountMergeRequestStatus.Accepted,
                "merging the pair must auto-close its pending request — no orphan is left behind");
    }

    // ==================================================================
    // Helpers
    // ==================================================================

    private async Task<Guid> SeedAdminUserAsync()
    {
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
                "Failed to seed admin user for ReconcileOrphanTests: "
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
        await db.SaveChangesAsync();
        return adminId;
    }
}
