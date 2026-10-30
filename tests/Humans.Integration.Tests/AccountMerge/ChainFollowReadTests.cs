using Humans.Auth.Data;
using Humans.Auth.Domain;
using AwesomeAssertions;
using Humans.Consent.Services;
using Humans.AuditLog.Contracts;
using Humans.AuditLog.Services;
using Humans.Budget.Contracts;
using Humans.Gdpr.Contracts;
using Humans.Base.Constants;
using Humans.Integration.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Xunit;
using Humans.Users.Contracts;

namespace Humans.Integration.Tests.AccountMerge;

/// <summary>
/// Per-user reads on AuditLog, Consent and BudgetAuditLog surface the merged source's rows through
/// <c>UserInfo.MergedUserIds</c>.
/// </summary>
public class ChainFollowReadTests(HumansTestDatabase database) : IntegrationTestBase(database)
{
    [HumansFact(Timeout = 30_000)]
    public async Task AuditLog_ReadByUserId_FollowsMergedToUserIdChain()
    {
        var description = $"chain-follow-audit-{Guid.NewGuid():N}";

        var (sourceId, targetId) = await Factory.SeedMergeFixtureAsync(b =>
        {
            b.WithSourceAuditLogEntry("AccountAnonymized", description);
        });
        var requestId = await Factory.SeedMergeRequestAsync(sourceId, targetId);

        var adminId = await SeedAdminUserAsync();
        await AcceptAsync(requestId, adminId);

        await using var assertScope = Factory.Services.CreateAsyncScope();
        var auditReader = assertScope.ServiceProvider.GetRequiredService<IAuditLogReader>();
        var entries = await auditReader.GetByUserAsync(targetId, count: 100, ct: TestContext.Current.CancellationToken);

        entries.Should().Contain(
            e => e.Description == description
                 && e.EntityType == "User"
                 && e.EntityId == sourceId,
            "chain-follow surfaces the source-attributed audit entry under target reads");
    }

    [HumansFact(Timeout = 30_000)]
    public async Task ConsentRecords_ReadByUserId_FollowsMergedToUserIdChain()
    {
        Guid versionId;

        var (sourceId, targetId) = await Factory.SeedMergeFixtureAsync();

        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var builder = new MergeFixtureBuilder(scope, sourceId, targetId);
            versionId = builder.SeedDocumentVersionNow($"ChainFollowDoc-{Guid.NewGuid():N}".Substring(0, 24));
            builder.WithSourceConsentRecord(versionId);
            await builder.SaveAllAsync();
        }

        var requestId = await Factory.SeedMergeRequestAsync(sourceId, targetId);

        var adminId = await SeedAdminUserAsync();
        await AcceptAsync(requestId, adminId);

        // Only the source consented to this version, so a match under a target read proves the union.
        await using var assertScope = Factory.Services.CreateAsyncScope();
        var consentService = assertScope.ServiceProvider.GetRequiredService<IConsentManagement>();
        var dashboard = await consentService.GetConsentDashboardAsync(targetId, TestContext.Current.CancellationToken);

        dashboard.History.Should().Contain(
            h => h.DocumentVersionId == versionId,
            "chain-follow surfaces the source-attributed consent record under target reads");
    }

    // Only the GDPR contributor path chain-follows; the year-scoped GetAuditLogAsync is not a per-user read.
    [HumansFact(Timeout = 30_000)]
    public async Task BudgetAuditLog_ContributeForUserAsync_FollowsMergedToUserIdChain()
    {
        var description = $"chain-follow-budget-{Guid.NewGuid():N}";

        var (sourceId, targetId) = await Factory.SeedMergeFixtureAsync();

        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var builder = new MergeFixtureBuilder(scope, sourceId, targetId);
            var budgetYearId = builder.SeedBudgetYearNow($"BY-{Guid.NewGuid():N}".Substring(0, 6));
            builder.WithSourceBudgetAuditLog(budgetYearId, description);
            await builder.SaveAllAsync();
        }

        var requestId = await Factory.SeedMergeRequestAsync(sourceId, targetId);

        var adminId = await SeedAdminUserAsync();
        await AcceptAsync(requestId, adminId);

        // BudgetService has one DI registration, as IBudgetService; cast for the contributor method.
        await using var assertScope = Factory.Services.CreateAsyncScope();
        var budgetService = assertScope.ServiceProvider.GetRequiredService<IBudgetService>();
        var contributor = (IUserDataContributor)budgetService;
        var slices = await contributor.ContributeForUserAsync(targetId, TestContext.Current.CancellationToken);

        // The fold never mutates the row, so chain-follow is the only way the target's export sees it.
        slices.Should().ContainSingle();
        var entries = slices[0].Data as System.Collections.IEnumerable;
        entries.Should().NotBeNull();

        var found = entries.Cast<object>()
            .Select(o => o.GetType().GetProperty("Description")?.GetValue(o) as string)
            .Where(d => d is not null);
        found.Should().Contain(description,
            "chain-follow surfaces the source-attributed BudgetAuditLog row under target's GDPR export");
    }

    private async Task<Guid> SeedAdminUserAsync()
    {
        // An admin user with an active Admin role: AcceptAsync FKs the request and audit row to it, and team folds
        // need an actor allowed to remove members.
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
                "Failed to seed admin user for ChainFollowReadTests: "
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

    private async Task AcceptAsync(Guid requestId, Guid adminUserId)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var mergeService = scope.ServiceProvider.GetRequiredService<IAccountMergeService>();
        // These tests assert source to target, so the request target survives.
        var request = await mergeService.GetByIdAsync(requestId, TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException($"Merge request {requestId} not found.");
        await mergeService.AcceptAsync(requestId, adminUserId, survivorUserId: request.TargetUser.Id, ct: TestContext.Current.CancellationToken);
    }
}
