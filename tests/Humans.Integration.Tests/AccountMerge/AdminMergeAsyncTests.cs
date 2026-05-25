using AwesomeAssertions;
using Humans.Application.Interfaces.Profiles;
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
/// Integration test for <see cref="IAccountMergeService.AdminMergeAsync"/>:
/// seeds a full two-user fixture, invokes the admin-initiated fold, and
/// asserts all six post-conditions from the EmailProblems spec (case 5).
/// </summary>
public class AdminMergeAsyncTests(HumansWebApplicationFactory factory) : IClassFixture<HumansWebApplicationFactory>
{
    [HumansFact(Timeout = 60_000)]
    public async Task AdminMergeAsync_FullFixture_AllPostConditionsHold()
    {
        var runTag = Guid.NewGuid().ToString("N");
        var sourceEmail = $"joe-{runTag}@x.com";
        var targetEmail = $"target-{runTag}@x.com";
        var sourceLoginKey = $"login-src-{runTag}";
        var sourceOnlyRole = $"role-src-{runTag}";

        Guid sourceOnlyTeamId = Guid.Empty;

        // Stage 1 — seed source/target user pair with their primary verified
        // emails, a login, and a role assignment.
        var (sourceId, targetId) = await factory.SeedMergeFixtureAsync(b =>
        {
            // Source: primary verified email.
            b.WithSourceEmail(sourceEmail, verified: true, isPrimary: true);

            // Target: primary verified email (with IsGoogle so preservation
            // of IsPrimary + IsGoogle is assertable).
            b.WithTargetEmail(targetEmail, verified: true, isPrimary: true, isGoogle: true);

            // Source login — must move to target.
            b.WithSourceLogin("Google", sourceLoginKey);

            // Source role assignment — must re-FK to target.
            b.WithSourceRoleAssignment(sourceOnlyRole);
        });

        // Stage 2 — seed a source-only team membership (requires ad-hoc Team row).
        await using (var seedScope = factory.Services.CreateAsyncScope())
        {
            var builder = new MergeFixtureBuilder(seedScope, sourceId, targetId);
            sourceOnlyTeamId = builder.SeedTeamNow($"SrcOnly-{runTag}".Substring(0, 12));
            builder.WithSourceTeamMember(sourceOnlyTeamId);
            await builder.SaveAllAsync();
        }

        // Act — admin-initiated merge (no AccountMergeRequest).
        var adminId = await SeedAdminUserAsync();
        await using (var actScope = factory.Services.CreateAsyncScope())
        {
            var mergeService = actScope.ServiceProvider.GetRequiredService<IAccountMergeService>();
            await mergeService.AdminMergeAsync(sourceId, targetId, adminId);
        }

        // Assert — all six post-conditions from the EmailProblems spec case 5.
        await using var assertScope = factory.Services.CreateAsyncScope();
        var db = assertScope.ServiceProvider.GetRequiredService<HumansDbContext>();

        // ----------------------------------------------------------------
        // Post-condition 1: exactly one UserEmail row per normalized email.
        // ----------------------------------------------------------------
        var sourceEmailRows = await db.UserEmails.AsNoTracking()
            .Where(e => e.Email == sourceEmail).ToListAsync();
        sourceEmailRows.Should().ContainSingle(
            "the source-only email should exist exactly once (re-FK'd to target)");
        sourceEmailRows[0].UserId.Should().Be(targetId,
            "source-only email must be re-FK'd onto the target user");

        var targetEmailRows = await db.UserEmails.AsNoTracking()
            .Where(e => e.Email == targetEmail).ToListAsync();
        targetEmailRows.Should().ContainSingle(
            "target's primary email should exist exactly once");

        // ----------------------------------------------------------------
        // Post-condition 2: source has zero UserEmail rows.
        // ----------------------------------------------------------------
        (await db.UserEmails.AsNoTracking().CountAsync(e => e.UserId == sourceId))
            .Should().Be(0, "all UserEmails must be moved off the source user");

        // ----------------------------------------------------------------
        // Post-condition 3: source has zero AspNetUserLogins rows.
        // ----------------------------------------------------------------
        (await db.Set<IdentityUserLogin<Guid>>().AsNoTracking().CountAsync(l => l.UserId == sourceId))
            .Should().Be(0, "all logins must be re-FK'd to target");

        (await db.Set<IdentityUserLogin<Guid>>().AsNoTracking()
                .AnyAsync(l => l.UserId == targetId
                    && l.LoginProvider == "Google" && l.ProviderKey == sourceLoginKey))
            .Should().BeTrue("source login must be re-FK'd to target");

        // ----------------------------------------------------------------
        // Post-condition 4: target's pre-existing IsPrimary / IsGoogle preserved.
        // ----------------------------------------------------------------
        var survivingTargetEmail = await db.UserEmails.AsNoTracking()
            .FirstOrDefaultAsync(e => e.UserId == targetId && e.Email == targetEmail);
        survivingTargetEmail.Should().NotBeNull();
        survivingTargetEmail!.IsPrimary.Should().BeTrue(
            "target's pre-existing IsPrimary must remain true after fold");
        survivingTargetEmail.IsGoogle.Should().BeTrue(
            "target's pre-existing IsGoogle must remain true after fold");

        // ----------------------------------------------------------------
        // Post-condition 5: source tombstoned with MergedToUserId + MergedAt.
        // ----------------------------------------------------------------
        var sourceUser = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == sourceId);
        sourceUser.Should().NotBeNull();
        sourceUser!.MergedToUserId.Should().Be(targetId,
            "source must be tombstoned pointing at the target");
        sourceUser.MergedAt.Should().NotBeNull(
            "source MergedAt must be set after admin-initiated fold");

        // ----------------------------------------------------------------
        // Post-condition 6: AuditLogEntry with AccountMergeAccepted action,
        // EntityType == nameof(User), EntityId == sourceId, and description
        // starting with "Admin-initiated via EmailProblems".
        // ----------------------------------------------------------------
        var auditRow = await db.AuditLogEntries.AsNoTracking()
            .FirstOrDefaultAsync(a =>
                a.Action == AuditAction.AccountMergeAccepted
                && a.EntityType == nameof(User)
                && a.EntityId == sourceId);
        auditRow.Should().NotBeNull("an AccountMergeAccepted audit row must be written for the source");
        auditRow!.Description.Should().StartWith("Admin-initiated via EmailProblems",
            "the audit description must identify this as an admin-initiated fold");

        // Bonus: source's team membership and role assignment moved to target.
        (await db.TeamMembers.AsNoTracking()
                .AnyAsync(tm => tm.UserId == targetId && tm.TeamId == sourceOnlyTeamId
                    && tm.LeftAt == null))
            .Should().BeTrue("source-only team membership must be re-FK'd to target");

        (await db.RoleAssignments.AsNoTracking()
                .AnyAsync(ra => ra.UserId == targetId && ra.RoleName == sourceOnlyRole))
            .Should().BeTrue("source-only role assignment must be re-FK'd to target");
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
                "Failed to seed admin user for AdminMergeAsyncTests: "
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
