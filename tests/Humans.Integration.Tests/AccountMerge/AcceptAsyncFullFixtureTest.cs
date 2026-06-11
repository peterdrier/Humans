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
/// Phase 6.4 of the AccountMergeService fold-into-target redesign: a single
/// end-to-end integration test that seeds the source user with a row in every
/// fixture-supported section (plus a handful of target-side collisions to
/// exercise dedup paths), accepts the merge, and asserts a comprehensive set
/// of post-conditions:
/// <list type="bullet">
///   <item>Source <see cref="User"/> tombstoned with <c>MergedToUserId</c>,
///         <c>MergedAt</c>, and far-future <c>LockoutEnd</c>.</item>
///   <item>Live cross-section tables hold no rows attributed to source.</item>
///   <item>Append-only sections (AuditLog, ConsentRecord, BudgetAuditLog)
///         still hold source's rows for the chain-follow read path.</item>
///   <item>Target picked up source's content via re-FK / dedup / move
///         depending on the per-rule fold semantics.</item>
/// </list>
/// <para>
/// Sections deferred (require heavy parent-row setup): Tickets,
/// VolunteerEventProfile / VolunteerTagPreference, GeneralAvailability, and
/// CampLead / CampRoleAssignment. These are covered by their own dedicated
/// per-rule tests when the fixture builder grows support.
/// </para>
/// </summary>
public class AcceptAsyncFullFixtureTest(HumansWebApplicationFactory factory)
    : IClassFixture<HumansWebApplicationFactory>
{
    [HumansFact(Timeout = 60_000)]
    public async Task AcceptAsync_FullFixture_FoldsAllSectionsAndTombstonesSource()
    {
        // Per-test unique strings so this test can share a class-fixture DB
        // with its siblings without colliding on uniqueness indices /
        // description-based assertions.
        var runTag = Guid.NewGuid().ToString("N");
        var sourceOnlyEmail = $"src-only-{runTag}@example.com";
        var sharedEmail = $"shared-{runTag}@example.com";
        var sourceLoginKey = $"login-src-{runTag}";
        var auditDescription = $"audit-src-{runTag}";
        var budgetDescription = $"budget-src-{runTag}";
        var sharedRole = $"role-shared-{runTag}";
        var sourceOnlyRole = $"role-src-{runTag}";

        Guid sharedTeamId;
        Guid sourceOnlyTeamId;
        Guid joinTeamId;
        Guid sharedNotificationId;
        Guid sourceOnlyNotificationId;
        Guid contestedCampaignId;
        Guid sourceOnlyCampaignId;
        Guid feedbackReportId;
        Guid budgetYearId;
        Guid documentVersionId;

        // Stage 1 — seed source/target user pair plus all per-section data
        // that does NOT need an ad-hoc parent (UserEmails, ContactFields,
        // VolunteerHistory, ProfileLanguages, CommPrefs, IdentityUserLogins,
        // EventParticipations, Applications, AuditLogEntries, RoleAssignments).
        var (sourceId, targetId) = await factory.SeedMergeFixtureAsync(b =>
        {
            // UserEmail — shared (collapse) + source-only (re-FK).
            b.WithSourceEmail(sharedEmail, verified: true);
            b.WithTargetEmail(sharedEmail, verified: false, isPrimary: true);
            b.WithSourceEmail(sourceOnlyEmail, verified: true);

            // ContactField — source-only Telegram + shared Phone.
            b.WithSourceContactField(ContactFieldType.Telegram, $"@src-{runTag}");
            b.WithTargetContactField(ContactFieldType.Phone, $"+34 600 100 {runTag.Substring(0, 3)}");
            b.WithSourceContactField(ContactFieldType.Phone, $"+34 600 100 {runTag.Substring(0, 3)}");

            // VolunteerHistory — source-only entry.
            b.WithSourceVolunteerHistory(2024, $"BurnFest-{runTag}");

            // ProfileLanguage — source-only language.
            b.WithSourceLanguage($"x-{runTag.Substring(0, 4)}", LanguageProficiency.Native);

            // CommunicationPreference — source-only category.
            b.WithSourceCommPref(MessageCategory.VolunteerUpdates, optedOut: true);

            // IdentityUserLogin — source-only key (logins keyed on
            // (provider, key); two users can never share the same key).
            b.WithSourceLogin("Google", sourceLoginKey);

            // EventParticipation — source-only year.
            b.WithSourceEventParticipation(2024, ParticipationStatus.Attended);

            // Application — source-only.
            b.WithSourceApplication();

            // AuditLog — append-only; chain-follow stitches at read time.
            b.WithSourceAuditLogEntry(AuditAction.AccountAnonymized, auditDescription);

            // RoleAssignment — shared (drop source) + source-only (re-FK).
            b.WithSourceRoleAssignment(sharedRole);
            b.WithTargetRoleAssignment(sharedRole);
            b.WithSourceRoleAssignment(sourceOnlyRole);
        });

        // Stage 2 — seed the per-section data that needs ad-hoc parent rows
        // (Teams, Notifications, Campaigns, FeedbackMessages, BudgetAuditLog,
        // ConsentRecord). Mirrors the patterns in AcceptAsyncFoldTests for
        // each section.
        await using (var seedScope = factory.Services.CreateAsyncScope())
        {
            var builder = new MergeFixtureBuilder(seedScope, sourceId, targetId);

            // Teams: shared (drop source slot) + source-only (re-FK).
            sharedTeamId = builder.SeedTeamNow($"Shared-{runTag}".Substring(0, 12));
            sourceOnlyTeamId = builder.SeedTeamNow($"SrcOnly-{runTag}".Substring(0, 12));
            joinTeamId = builder.SeedTeamNow($"Join-{runTag}".Substring(0, 12));
            builder.WithSourceTeamMember(sharedTeamId);
            builder.WithTargetTeamMember(sharedTeamId);
            builder.WithSourceTeamMember(sourceOnlyTeamId);
            builder.WithSourceTeamJoinRequest(joinTeamId);

            // Notifications: shared (drop dup) + source-only (re-FK).
            sharedNotificationId = builder.SeedNotificationNow($"shared-{runTag}");
            sourceOnlyNotificationId = builder.SeedNotificationNow($"src-{runTag}");
            builder.WithSourceNotificationRecipient(sharedNotificationId);
            builder.WithTargetNotificationRecipient(sharedNotificationId);
            builder.WithSourceNotificationRecipient(sourceOnlyNotificationId);

            // Campaigns: contested (dedup) + source-only (re-FK). Use source
            // as creator — fold doesn't touch Campaigns themselves.
            contestedCampaignId = builder.SeedCampaignNow($"Contested-{runTag}", sourceId);
            sourceOnlyCampaignId = builder.SeedCampaignNow($"SrcOnly-{runTag}", sourceId);
            builder.WithSourceCampaignGrant(contestedCampaignId);
            builder.WithTargetCampaignGrant(contestedCampaignId);
            builder.WithSourceCampaignGrant(sourceOnlyCampaignId);

            // FeedbackReport (target-owned) + source FeedbackMessage on it.
            feedbackReportId = builder.SeedFeedbackReportNow(targetId, $"report-{runTag}");
            builder.WithSourceFeedbackMessage(feedbackReportId, $"src-msg-{runTag}");

            // Source's own FeedbackReport — should re-FK to target.
            builder.WithSourceFeedbackReport($"src-report-{runTag}");

            // BudgetAuditLog — append-only; chain-follow at read time.
            budgetYearId = builder.SeedBudgetYearNow($"BY-{runTag}".Substring(0, 6));
            builder.WithSourceBudgetAuditLog(budgetYearId, budgetDescription);

            // ConsentRecord — append-only; chain-follow at read time.
            documentVersionId = builder.SeedDocumentVersionNow($"Doc-{runTag}".Substring(0, 16));
            builder.WithSourceConsentRecord(documentVersionId);

            await builder.SaveAllAsync();
        }

        var requestId = await factory.SeedMergeRequestAsync(sourceId, targetId);

        // Act — accept the merge.
        var adminId = await SeedAdminUserAsync();
        await using (var actScope = factory.Services.CreateAsyncScope())
        {
            var mergeService = actScope.ServiceProvider.GetRequiredService<IAccountMergeService>();
            await mergeService.AcceptAsync(requestId, adminId, survivorUserId: targetId, ct: TestContext.Current.CancellationToken);
        }

        // Assert — comprehensive post-merge state.
        await using var assertScope = factory.Services.CreateAsyncScope();
        var db = assertScope.ServiceProvider.GetRequiredService<HumansDbContext>();

        // ----------------------------------------------------------------
        // Source User: tombstoned with MergedToUserId, MergedAt, lockout.
        // ----------------------------------------------------------------
        var sourceUser = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == sourceId, TestContext.Current.CancellationToken);
        sourceUser.Should().NotBeNull();
        sourceUser.MergedToUserId.Should().Be(targetId);
        sourceUser.MergedAt.Should().NotBeNull();
        sourceUser.LockoutEnabled.Should().BeTrue();
        sourceUser.LockoutEnd.Should().NotBeNull();
        // AnonymizeForMergeAsync sets LockoutEnd = DateTimeOffset.MaxValue —
        // assert "far future" rather than the exact value.
        sourceUser.LockoutEnd!.Value.Should().BeAfter(DateTimeOffset.UtcNow.AddYears(50));

        // Target User: still active, no lockout, no merged-to.
        var targetUser = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == targetId, TestContext.Current.CancellationToken);
        targetUser.Should().NotBeNull();
        targetUser.MergedToUserId.Should().BeNull();
        targetUser.MergedAt.Should().BeNull();

        // ----------------------------------------------------------------
        // Live cross-section tables: source row count == 0.
        // ----------------------------------------------------------------
        (await db.UserEmails.AsNoTracking().CountAsync(e => e.UserId == sourceId, TestContext.Current.CancellationToken))
            .Should().Be(0, "all UserEmails re-FK or collapse off the source");
        (await db.Set<IdentityUserLogin<Guid>>().AsNoTracking().CountAsync(l => l.UserId == sourceId, TestContext.Current.CancellationToken))
            .Should().Be(0, "all logins re-FK to target");
        (await db.CommunicationPreferences.AsNoTracking().CountAsync(cp => cp.UserId == sourceId, TestContext.Current.CancellationToken))
            .Should().Be(0);
        (await db.EventParticipations.AsNoTracking().CountAsync(ep => ep.UserId == sourceId, TestContext.Current.CancellationToken))
            .Should().Be(0);
        (await db.Applications.AsNoTracking().CountAsync(a => a.UserId == sourceId, TestContext.Current.CancellationToken))
            .Should().Be(0);
        (await db.RoleAssignments.AsNoTracking().CountAsync(ra =>
                ra.UserId == sourceId && (ra.RoleName == sharedRole || ra.RoleName == sourceOnlyRole), TestContext.Current.CancellationToken))
            .Should().Be(0);
        (await db.TeamMembers.AsNoTracking().CountAsync(tm =>
                tm.UserId == sourceId && tm.LeftAt == null
                && (tm.TeamId == sharedTeamId || tm.TeamId == sourceOnlyTeamId), TestContext.Current.CancellationToken))
            .Should().Be(0, "no active source memberships remain");
        (await db.TeamJoinRequests.AsNoTracking().CountAsync(r =>
                r.UserId == sourceId && r.TeamId == joinTeamId, TestContext.Current.CancellationToken))
            .Should().Be(0);
        (await db.NotificationRecipients.AsNoTracking().CountAsync(nr =>
                nr.UserId == sourceId
                && (nr.NotificationId == sharedNotificationId || nr.NotificationId == sourceOnlyNotificationId), TestContext.Current.CancellationToken))
            .Should().Be(0);
        (await db.CampaignGrants.AsNoTracking().CountAsync(g =>
                g.UserId == sourceId
                && (g.CampaignId == contestedCampaignId || g.CampaignId == sourceOnlyCampaignId), TestContext.Current.CancellationToken))
            .Should().Be(0);
        (await db.FeedbackReports.AsNoTracking().CountAsync(r => r.UserId == sourceId, TestContext.Current.CancellationToken))
            .Should().Be(0);
        (await db.FeedbackMessages.AsNoTracking().CountAsync(m => m.SenderUserId == sourceId, TestContext.Current.CancellationToken))
            .Should().Be(0);

        // Profile is the one live-section EXCEPTION — source profile row
        // stays as a tombstone with anonymized scalars; per-Profile child
        // rows (ContactFields, ProfileLanguages, VolunteerHistoryEntries)
        // re-FK to the target profile.
        var sourceProfileId = await db.Profiles.AsNoTracking()
            .Where(p => p.UserId == sourceId).Select(p => p.Id).SingleAsync(TestContext.Current.CancellationToken);
        var targetProfileId = await db.Profiles.AsNoTracking()
            .Where(p => p.UserId == targetId).Select(p => p.Id).SingleAsync(TestContext.Current.CancellationToken);

        (await db.ContactFields.AsNoTracking().CountAsync(cf => cf.ProfileId == sourceProfileId, TestContext.Current.CancellationToken))
            .Should().Be(0);
        (await db.ProfileLanguages.AsNoTracking().CountAsync(pl => pl.ProfileId == sourceProfileId, TestContext.Current.CancellationToken))
            .Should().Be(0);
        (await db.VolunteerHistoryEntries.AsNoTracking().CountAsync(v => v.ProfileId == sourceProfileId, TestContext.Current.CancellationToken))
            .Should().Be(0);

        // ----------------------------------------------------------------
        // Append-only sections: source rows still attached (chain-follow).
        // ----------------------------------------------------------------
        (await db.AuditLogEntries.AsNoTracking()
                .CountAsync(a => a.Description == auditDescription && a.ActorUserId == sourceId, TestContext.Current.CancellationToken))
            .Should().Be(1, "audit log is append-only — source row stays for chain-follow");
        (await db.ConsentRecords.AsNoTracking().CountAsync(c => c.UserId == sourceId, TestContext.Current.CancellationToken))
            .Should().BeGreaterThan(0, "consent records are append-only — source rows stay for chain-follow");
        (await db.BudgetAuditLogs.AsNoTracking()
                .CountAsync(b => b.Description == budgetDescription && b.ActorUserId == sourceId, TestContext.Current.CancellationToken))
            .Should().Be(1, "budget audit log is append-only — source row stays for chain-follow");

        // ----------------------------------------------------------------
        // Target picked up source content (one assertion per fold rule).
        // ----------------------------------------------------------------
        (await db.UserEmails.AsNoTracking().AnyAsync(e =>
                e.UserId == targetId && e.Email == sourceOnlyEmail, TestContext.Current.CancellationToken))
            .Should().BeTrue("source-only email re-FK'd onto target");

        var sharedEmailRows = await db.UserEmails.AsNoTracking()
            .Where(e => e.UserId == targetId && e.Email == sharedEmail)
            .ToListAsync(TestContext.Current.CancellationToken);
        sharedEmailRows.Should().ContainSingle("shared-email collapsed onto a single target row");
        sharedEmailRows[0].IsVerified.Should().BeTrue("source's verified flag OR-combines into the survivor");

        (await db.Set<IdentityUserLogin<Guid>>().AsNoTracking()
                .AnyAsync(l => l.UserId == targetId
                    && l.LoginProvider == "Google" && l.ProviderKey == sourceLoginKey, TestContext.Current.CancellationToken))
            .Should().BeTrue("source login re-FK'd to target");

        (await db.ContactFields.AsNoTracking()
                .AnyAsync(cf => cf.ProfileId == targetProfileId
                    && cf.FieldType == ContactFieldType.Telegram
                    && cf.Value == $"@src-{runTag}", TestContext.Current.CancellationToken))
            .Should().BeTrue("source-only Telegram contact field moved to target profile");

        (await db.VolunteerHistoryEntries.AsNoTracking()
                .AnyAsync(v => v.ProfileId == targetProfileId && v.EventName == $"BurnFest-{runTag}", TestContext.Current.CancellationToken))
            .Should().BeTrue("source volunteer-history row re-FK'd to target profile");

        (await db.ProfileLanguages.AsNoTracking()
                .AnyAsync(pl => pl.ProfileId == targetProfileId
                    && pl.LanguageCode == $"x-{runTag.Substring(0, 4)}", TestContext.Current.CancellationToken))
            .Should().BeTrue("source-only language re-FK'd to target profile");

        (await db.CommunicationPreferences.AsNoTracking()
                .AnyAsync(cp => cp.UserId == targetId
                    && cp.Category == MessageCategory.VolunteerUpdates && cp.OptedOut, TestContext.Current.CancellationToken))
            .Should().BeTrue("source CommPref re-FK'd to target");

        (await db.EventParticipations.AsNoTracking()
                .AnyAsync(ep => ep.UserId == targetId && ep.Year == 2024
                    && ep.Status == ParticipationStatus.Attended, TestContext.Current.CancellationToken))
            .Should().BeTrue("source event participation re-FK'd to target");

        (await db.Applications.AsNoTracking()
                .CountAsync(a => a.UserId == targetId
                    && a.MembershipTier == MembershipTier.Colaborador, TestContext.Current.CancellationToken))
            .Should().BeGreaterThan(0, "source application moved to target");

        (await db.RoleAssignments.AsNoTracking()
                .AnyAsync(ra => ra.UserId == targetId && ra.RoleName == sourceOnlyRole, TestContext.Current.CancellationToken))
            .Should().BeTrue("source-only role re-FK'd to target");
        (await db.RoleAssignments.AsNoTracking()
                .CountAsync(ra => ra.UserId == targetId && ra.RoleName == sharedRole, TestContext.Current.CancellationToken))
            .Should().Be(1, "shared role stays as target's single active assignment");

        (await db.TeamMembers.AsNoTracking()
                .AnyAsync(tm => tm.UserId == targetId && tm.TeamId == sourceOnlyTeamId
                    && tm.LeftAt == null, TestContext.Current.CancellationToken))
            .Should().BeTrue("target gained active membership on source-only team");
        (await db.TeamMembers.AsNoTracking()
                .CountAsync(tm => tm.UserId == targetId && tm.TeamId == sharedTeamId
                    && tm.LeftAt == null, TestContext.Current.CancellationToken))
            .Should().Be(1, "target keeps its single active membership on shared team");

        (await db.TeamJoinRequests.AsNoTracking()
                .AnyAsync(r => r.UserId == targetId && r.TeamId == joinTeamId, TestContext.Current.CancellationToken))
            .Should().BeTrue("source's team-join request re-FK'd to target");

        (await db.NotificationRecipients.AsNoTracking()
                .AnyAsync(nr => nr.UserId == targetId
                    && nr.NotificationId == sourceOnlyNotificationId, TestContext.Current.CancellationToken))
            .Should().BeTrue("source-only notification recipient re-FK'd to target");
        (await db.NotificationRecipients.AsNoTracking()
                .CountAsync(nr => nr.UserId == targetId
                    && nr.NotificationId == sharedNotificationId, TestContext.Current.CancellationToken))
            .Should().Be(1, "duplicate-on-shared-notification dropped, single row kept");

        (await db.CampaignGrants.AsNoTracking()
                .AnyAsync(g => g.UserId == targetId && g.CampaignId == sourceOnlyCampaignId, TestContext.Current.CancellationToken))
            .Should().BeTrue("source-only campaign grant re-FK'd to target");
        (await db.CampaignGrants.AsNoTracking()
                .CountAsync(g => g.UserId == targetId && g.CampaignId == contestedCampaignId, TestContext.Current.CancellationToken))
            .Should().Be(1, "contested campaign grant deduped to a single target row");

        (await db.FeedbackReports.AsNoTracking()
                .AnyAsync(r => r.UserId == targetId && r.Description == $"src-report-{runTag}", TestContext.Current.CancellationToken))
            .Should().BeTrue("source feedback report re-FK'd to target");
        (await db.FeedbackMessages.AsNoTracking()
                .AnyAsync(m => m.SenderUserId == targetId && m.Content == $"src-msg-{runTag}", TestContext.Current.CancellationToken))
            .Should().BeTrue("source feedback message re-FK'd to target");

        // ----------------------------------------------------------------
        // Source profile tombstone — anonymized scalars.
        // ----------------------------------------------------------------
        var sourceProfile = await db.Profiles.AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == sourceId, TestContext.Current.CancellationToken);
        sourceProfile.Should().NotBeNull("source profile row stays as a tombstone");
        sourceProfile.FirstName.Should().Be("Merged");
        sourceProfile.LastName.Should().Be("User");
    }

    // ==================================================================
    // Helpers — mirrors AcceptAsyncFoldTests / ChainFollowReadTests
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
                "Failed to seed admin user for AcceptAsyncFullFixtureTest: "
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
