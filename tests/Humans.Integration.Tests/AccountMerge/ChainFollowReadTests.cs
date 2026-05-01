using AwesomeAssertions;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Budget;
using Humans.Application.Interfaces.Consent;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Profiles;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Integration.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Xunit;

namespace Humans.Integration.Tests.AccountMerge;

/// <summary>
/// Phase 6.3 of the AccountMergeService fold-into-target redesign:
/// integration tests that verify per-user reads on AuditLog, Consent, and
/// BudgetAuditLog correctly union source-tombstone ids via
/// <c>IUserService.GetMergedSourceIdsAsync</c> after a fold.
///
/// Each test seeds a source/target user pair plus a per-section row attached
/// to the source, runs <c>AcceptAsync</c> (which sets
/// <c>User.MergedToUserId</c> on the source via
/// <c>IUserService.AnonymizeForMergeAsync</c>), then queries the read path
/// for the target and asserts the source-attributed row surfaces.
/// </summary>
public class ChainFollowReadTests : IClassFixture<HumansWebApplicationFactory>
{
    private readonly HumansWebApplicationFactory _factory;

    public ChainFollowReadTests(HumansWebApplicationFactory factory) => _factory = factory;

    // ==================================================================
    // AuditLog — chain-follow GetByUserAsync (Phase 4.1)
    // ==================================================================

    [HumansFact(Timeout = 30_000)]
    public async Task AuditLog_ReadByUserId_FollowsMergedToUserIdChain()
    {
        // Per-test description so the post-merge query doesn't pick up rows
        // seeded by other tests in the same shared-DB class fixture.
        var description = $"chain-follow-audit-{Guid.NewGuid():N}";

        var (sourceId, targetId) = await _factory.SeedMergeFixtureAsync(b =>
        {
            b.WithSourceAuditLogEntry(AuditAction.AccountAnonymized, description);
        });
        var requestId = await _factory.SeedMergeRequestAsync(sourceId, targetId);

        var adminId = await SeedAdminUserAsync();
        await AcceptAsync(requestId, adminId);

        // Act: query the AuditLog read path for the TARGET — chain-follow
        // should union the source-tombstone id and surface source's row.
        await using var assertScope = _factory.Services.CreateAsyncScope();
        var auditService = assertScope.ServiceProvider.GetRequiredService<IAuditLogService>();
        var entries = await auditService.GetByUserAsync(targetId, count: 100);

        entries.Should().Contain(
            e => e.Description == description
                 && e.EntityType == "User"
                 && e.EntityId == sourceId,
            "chain-follow surfaces the source-attributed audit entry under target reads");
    }

    // ==================================================================
    // Consent — chain-follow GetUserConsentRecordsAsync (Phase 4.2)
    // ==================================================================

    [HumansFact(Timeout = 30_000)]
    public async Task ConsentRecords_ReadByUserId_FollowsMergedToUserIdChain()
    {
        Guid versionId = Guid.Empty;

        var (sourceId, targetId) = await _factory.SeedMergeFixtureAsync();

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var builder = new MergeFixtureBuilder(scope, sourceId, targetId);
            versionId = builder.SeedDocumentVersionNow($"ChainFollowDoc-{Guid.NewGuid():N}".Substring(0, 24));
            builder.WithSourceConsentRecord(versionId);
            await builder.SaveAllAsync();
        }

        var requestId = await _factory.SeedMergeRequestAsync(sourceId, targetId);

        var adminId = await SeedAdminUserAsync();
        await AcceptAsync(requestId, adminId);

        // Act: per-user consent read for the TARGET should surface source's
        // append-only consent record via the chain-follow union.
        await using var assertScope = _factory.Services.CreateAsyncScope();
        var consentService = assertScope.ServiceProvider.GetRequiredService<IConsentService>();
        var records = await consentService.GetUserConsentRecordsAsync(targetId);

        records.Should().Contain(
            r => r.UserId == sourceId && r.DocumentVersionId == versionId,
            "chain-follow surfaces the source-attributed consent record under target reads");
    }

    // ==================================================================
    // BudgetAuditLog — chain-follow ContributeForUserAsync (Phase 4.3)
    //
    // Only the GDPR contributor path chain-follows; the year-scoped
    // GetAuditLogAsync(budgetYearId) does NOT (it's not a per-user read).
    // ==================================================================

    [HumansFact(Timeout = 30_000)]
    public async Task BudgetAuditLog_ContributeForUserAsync_FollowsMergedToUserIdChain()
    {
        var description = $"chain-follow-budget-{Guid.NewGuid():N}";

        var (sourceId, targetId) = await _factory.SeedMergeFixtureAsync();

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var builder = new MergeFixtureBuilder(scope, sourceId, targetId);
            var budgetYearId = builder.SeedBudgetYearNow($"BY-{Guid.NewGuid():N}".Substring(0, 6));
            builder.WithSourceBudgetAuditLog(budgetYearId, description);
            await builder.SaveAllAsync();
        }

        var requestId = await _factory.SeedMergeRequestAsync(sourceId, targetId);

        var adminId = await SeedAdminUserAsync();
        await AcceptAsync(requestId, adminId);

        // Act: GDPR contributor path for the TARGET should surface the
        // source-attributed BudgetAuditLog row. BudgetService implements
        // both IBudgetService and IUserDataContributor; resolve the former
        // (single DI registration to that ID) and cast for the contributor
        // method.
        await using var assertScope = _factory.Services.CreateAsyncScope();
        var budgetService = assertScope.ServiceProvider.GetRequiredService<IBudgetService>();
        var contributor = (IUserDataContributor)budgetService;
        var slices = await contributor.ContributeForUserAsync(targetId, TestContext.Current.CancellationToken);

        // BudgetService.ContributeForUserAsync returns a single anonymized
        // slice whose Data is a List<{EntityType, FieldName, Description,
        // OccurredAt}>. The fold MUST NOT have mutated the row, so the
        // chain-follow is the only way the target's export sees it.
        slices.Should().ContainSingle();
        var entries = slices[0].Data as System.Collections.IEnumerable;
        entries.Should().NotBeNull();

        var found = entries!.Cast<object>()
            .Select(o => o.GetType().GetProperty("Description")?.GetValue(o) as string)
            .Where(d => d is not null);
        found.Should().Contain(description,
            "chain-follow surfaces the source-attributed BudgetAuditLog row under target's GDPR export");
    }

    // ==================================================================
    // Helpers — mirrors AcceptAsyncFoldTests
    // ==================================================================

    private async Task<Guid> SeedAdminUserAsync()
    {
        // AcceptAsync writes ResolvedByUserId = adminUserId on the merge request
        // and ActorUserId = adminUserId on the audit row, both FK'd to AspNetUsers.
        // It also calls TeamService.RemoveMemberAsync for non-system team folds,
        // which requires the actor to be Admin / Board / TeamsAdmin.
        await using var scope = _factory.Services.CreateAsyncScope();
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
                "Failed to seed admin user for ChainFollowReadTests: "
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

    private async Task AcceptAsync(Guid requestId, Guid adminUserId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var mergeService = scope.ServiceProvider.GetRequiredService<IAccountMergeService>();
        await mergeService.AcceptAsync(requestId, adminUserId);
    }
}
