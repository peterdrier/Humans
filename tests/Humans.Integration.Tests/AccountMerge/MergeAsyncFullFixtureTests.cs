using Humans.Auth.Data;
using Humans.Auth.Domain;
using Humans.AuditLog.Data;
using Humans.Teams.Data;
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
using Humans.AuditLog.Contracts;

namespace Humans.Integration.Tests.AccountMerge;

/// <summary>A direct admin fold, with no AccountMergeRequest.</summary>
public class MergeAsyncFullFixtureTests(HumansTestDatabase database) : IntegrationTestBase(database)
{
    [HumansFact(Timeout = 60_000)]
    public async Task MergeAsync_FullFixture_AllPostConditionsHold()
    {
        var runTag = Guid.NewGuid().ToString("N");
        var sourceEmail = $"joe-{runTag}@x.com";
        var targetEmail = $"target-{runTag}@x.com";
        var sourceLoginKey = $"login-src-{runTag}";
        var sourceOnlyRole = $"role-src-{runTag}";

        Guid sourceOnlyTeamId;

        var (sourceId, targetId) = await Factory.SeedMergeFixtureAsync(b =>
        {
            b.WithSourceEmail(sourceEmail, verified: true, isPrimary: true);

            // IsGoogle too, so preserving IsPrimary and IsGoogle is assertable.
            b.WithTargetEmail(targetEmail, verified: true, isPrimary: true, isGoogle: true);

            b.WithSourceLogin("Google", sourceLoginKey);

            b.WithSourceRoleAssignment(sourceOnlyRole);
        });

        await using (var seedScope = Factory.Services.CreateAsyncScope())
        {
            var builder = new MergeFixtureBuilder(seedScope, sourceId, targetId);
            sourceOnlyTeamId = builder.SeedTeamNow($"SrcOnly-{runTag}".Substring(0, 12));
            builder.WithSourceTeamMember(sourceOnlyTeamId);
            await builder.SaveAllAsync();
        }

        var adminId = await SeedAdminUserAsync();
        await using (var actScope = Factory.Services.CreateAsyncScope())
        {
            var mergeService = actScope.ServiceProvider.GetRequiredService<IAccountMergeService>();
            await mergeService.MergeAsync(targetId, sourceId, adminId, ct: TestContext.Current.CancellationToken);
        }

        // The six post-conditions of the EmailProblems spec, case 5.
        await using var assertScope = Factory.Services.CreateAsyncScope();
        var db = assertScope.ServiceProvider.GetRequiredService<UsersDbContext>();
        var teamsDb = assertScope.ServiceProvider.GetRequiredService<TeamsDbContext>();
        var authDb = assertScope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var auditLogDb = assertScope.ServiceProvider.GetRequiredService<AuditLogDbContext>();

        // 1: exactly one UserEmail row per normalized email.
        var sourceEmailRows = await db.UserEmails.AsNoTracking()
            .Where(e => e.Email == sourceEmail).ToListAsync(TestContext.Current.CancellationToken);
        sourceEmailRows.Should().ContainSingle(
            "the source-only email should exist exactly once (re-FK'd to target)");
        sourceEmailRows[0].UserId.Should().Be(targetId,
            "source-only email must be re-FK'd onto the target user");

        var targetEmailRows = await db.UserEmails.AsNoTracking()
            .Where(e => e.Email == targetEmail).ToListAsync(TestContext.Current.CancellationToken);
        targetEmailRows.Should().ContainSingle(
            "target's primary email should exist exactly once");

        // 2: the source has no UserEmail rows.
        (await db.UserEmails.AsNoTracking().CountAsync(e => e.UserId == sourceId, TestContext.Current.CancellationToken))
            .Should().Be(0, "all UserEmails must be moved off the source user");

        // 3: the source has no logins.
        (await db.Set<IdentityUserLogin<Guid>>().AsNoTracking().CountAsync(l => l.UserId == sourceId, TestContext.Current.CancellationToken))
            .Should().Be(0, "all logins must be re-FK'd to target");

        (await db.Set<IdentityUserLogin<Guid>>().AsNoTracking()
                .AnyAsync(l => l.UserId == targetId
                    && l.LoginProvider == "Google" && l.ProviderKey == sourceLoginKey, TestContext.Current.CancellationToken))
            .Should().BeTrue("source login must be re-FK'd to target");

        // 4: the target's IsPrimary and IsGoogle are preserved.
        var survivingTargetEmail = await db.UserEmails.AsNoTracking()
            .FirstOrDefaultAsync(e => e.UserId == targetId && e.Email == targetEmail, TestContext.Current.CancellationToken);
        survivingTargetEmail.Should().NotBeNull();
        survivingTargetEmail.IsPrimary.Should().BeTrue(
            "target's pre-existing IsPrimary must remain true after fold");
        survivingTargetEmail.IsGoogle.Should().BeTrue(
            "target's pre-existing IsGoogle must remain true after fold");

        // 5: the source is tombstoned.
        var sourceUser = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == sourceId, TestContext.Current.CancellationToken);
        sourceUser.Should().NotBeNull();
        sourceUser.MergedToUserId.Should().Be(targetId,
            "source must be tombstoned pointing at the target");
        sourceUser.MergedAt.Should().NotBeNull(
            "source MergedAt must be set after admin-initiated fold");

        // 6: an AccountMergeAccepted audit row on the archived account.
        var auditRow = await auditLogDb.AuditLogEntries.AsNoTracking()
            .FirstOrDefaultAsync(a =>
                a.Action == "AccountMergeAccepted"
                && a.EntityType == nameof(User)
                && a.EntityId == sourceId, TestContext.Current.CancellationToken);
        auditRow.Should().NotBeNull("an AccountMergeAccepted audit row must be written for the archived account");
        auditRow.Description.Should().StartWith($"Folded archived {sourceId} into survivor {targetId}",
            "the audit description records the archived→survivor fold");

        (await teamsDb.TeamMembers.AsNoTracking()
                .AnyAsync(tm => tm.UserId == targetId && tm.TeamId == sourceOnlyTeamId
                    && tm.LeftAt == null, TestContext.Current.CancellationToken))
            .Should().BeTrue("source-only team membership must be re-FK'd to target");

        (await authDb.RoleAssignments.AsNoTracking()
                .AnyAsync(ra => ra.UserId == targetId && ra.RoleName == sourceOnlyRole, TestContext.Current.CancellationToken))
            .Should().BeTrue("source-only role assignment must be re-FK'd to target");
    }

    private async Task<Guid> SeedAdminUserAsync()
    {
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
                "Failed to seed admin user for MergeAsyncFullFixtureTests: "
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
