using Humans.Email.Contracts;
using Humans.Governance.Contracts;
using Humans.Auth.Data;
using Humans.Auth.Domain;
using Humans.AuditLog.Data;
using Humans.Teams.Data;
using Humans.Notifications.Data;
using Humans.Consent.Data;
using Humans.Budget.Data;
using Humans.Campaigns.Data;
using Humans.Feedback.Data;
using Humans.Governance.Data;
using Humans.Tickets.Contracts;
using Humans.Tickets.Data;
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

/// <summary>
/// One merge with a source row in every fixture-supported section, plus target collisions for the dedup paths. Tickets,
/// volunteer event profiles and tag preferences, general availability and camp roles need heavier parents and are not
/// covered.
/// </summary>
public class AcceptAsyncFullFixtureTest(HumansTestDatabase database)
    : IntegrationTestBase(database)
{
    [HumansFact(Timeout = 60_000)]
    public async Task AcceptAsync_FullFixture_FoldsAllSectionsAndTombstonesSource()
    {
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

        // Rows that need no ad-hoc parent.
        var (sourceId, targetId) = await Factory.SeedMergeFixtureAsync(b =>
        {
            // Shared (collapses) plus source-only (re-FKs).
            b.WithSourceEmail(sharedEmail, verified: true);
            b.WithTargetEmail(sharedEmail, verified: false, isPrimary: true);
            b.WithSourceEmail(sourceOnlyEmail, verified: true);

            b.WithSourceContactField(ContactFieldType.Telegram, $"@src-{runTag}");
            b.WithTargetContactField(ContactFieldType.Phone, $"+34 600 100 {runTag.Substring(0, 3)}");
            b.WithSourceContactField(ContactFieldType.Phone, $"+34 600 100 {runTag.Substring(0, 3)}");

            b.WithSourceVolunteerHistory(2024, $"BurnFest-{runTag}");

            b.WithSourceLanguage($"x-{runTag.Substring(0, 4)}", LanguageProficiency.Native);

            b.WithSourceCommPref(LegacyMessageCategory.VolunteerUpdates, optedOut: true);

            // Logins are keyed on (provider, key), so two users never share one.
            b.WithSourceLogin("Google", sourceLoginKey);

            b.WithSourceEventParticipation(2024, LegacyParticipationStatus.Attended);

            b.WithSourceApplication();

            // Append-only; chain-follow stitches at read time.
            b.WithSourceAuditLogEntry("AccountAnonymized", auditDescription);

            // Shared (source drops) plus source-only (re-FKs).
            b.WithSourceRoleAssignment(sharedRole);
            b.WithTargetRoleAssignment(sharedRole);
            b.WithSourceRoleAssignment(sourceOnlyRole);
        });

        // Rows that need ad-hoc parents, seeded as in AcceptAsyncFoldTests.
        await using (var seedScope = Factory.Services.CreateAsyncScope())
        {
            var builder = new MergeFixtureBuilder(seedScope, sourceId, targetId);

            // Shared (source slot drops) plus source-only (re-FKs).
            sharedTeamId = builder.SeedTeamNow($"Shared-{runTag}".Substring(0, 12));
            sourceOnlyTeamId = builder.SeedTeamNow($"SrcOnly-{runTag}".Substring(0, 12));
            joinTeamId = builder.SeedTeamNow($"Join-{runTag}".Substring(0, 12));
            builder.WithSourceTeamMember(sharedTeamId);
            builder.WithTargetTeamMember(sharedTeamId);
            builder.WithSourceTeamMember(sourceOnlyTeamId);
            builder.WithSourceTeamJoinRequest(joinTeamId);

            // Shared (duplicate drops) plus source-only (re-FKs).
            sharedNotificationId = builder.SeedNotificationNow($"shared-{runTag}");
            sourceOnlyNotificationId = builder.SeedNotificationNow($"src-{runTag}");
            builder.WithSourceNotificationRecipient(sharedNotificationId);
            builder.WithTargetNotificationRecipient(sharedNotificationId);
            builder.WithSourceNotificationRecipient(sourceOnlyNotificationId);

            // Contested (dedups) plus source-only (re-FKs). The fold doesn't touch Campaigns themselves.
            contestedCampaignId = builder.SeedCampaignNow($"Contested-{runTag}", sourceId);
            sourceOnlyCampaignId = builder.SeedCampaignNow($"SrcOnly-{runTag}", sourceId);
            builder.WithSourceCampaignGrant(contestedCampaignId);
            builder.WithTargetCampaignGrant(contestedCampaignId);
            builder.WithSourceCampaignGrant(sourceOnlyCampaignId);

            // Target-owned report carrying a source message.
            feedbackReportId = builder.SeedFeedbackReportNow(targetId, $"report-{runTag}");
            builder.WithSourceFeedbackMessage(feedbackReportId, $"src-msg-{runTag}");

            builder.WithSourceFeedbackReport($"src-report-{runTag}");

            // Append-only, as is the consent record below; chain-follow at read time.
            budgetYearId = builder.SeedBudgetYearNow($"BY-{runTag}".Substring(0, 6));
            builder.WithSourceBudgetAuditLog(budgetYearId, budgetDescription);

            documentVersionId = builder.SeedDocumentVersionNow($"Doc-{runTag}".Substring(0, 16));
            builder.WithSourceConsentRecord(documentVersionId);

            await builder.SaveAllAsync();
        }

        var requestId = await Factory.SeedMergeRequestAsync(sourceId, targetId);

        var adminId = await SeedAdminUserAsync();
        await using (var actScope = Factory.Services.CreateAsyncScope())
        {
            var mergeService = actScope.ServiceProvider.GetRequiredService<IAccountMergeService>();
            await mergeService.AcceptAsync(requestId, adminId, survivorUserId: targetId, ct: TestContext.Current.CancellationToken);
        }

        await using var assertScope = Factory.Services.CreateAsyncScope();
        var db = assertScope.ServiceProvider.GetRequiredService<UsersDbContext>();
        var teamsDb = assertScope.ServiceProvider.GetRequiredService<TeamsDbContext>();
        var governanceDb = assertScope.ServiceProvider.GetRequiredService<GovernanceDbContext>();
        var ticketsDb = assertScope.ServiceProvider.GetRequiredService<TicketsDbContext>();
        var campaignsDb = assertScope.ServiceProvider.GetRequiredService<CampaignsDbContext>();
        var feedbackDb = assertScope.ServiceProvider.GetRequiredService<FeedbackDbContext>();
        var authDb = assertScope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var notificationsDb = assertScope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
        var budgetDb = assertScope.ServiceProvider.GetRequiredService<BudgetDbContext>();
        var legalDb = assertScope.ServiceProvider.GetRequiredService<LegalDbContext>();
        var auditLogDb = assertScope.ServiceProvider.GetRequiredService<AuditLogDbContext>();
        var emailDb = assertScope.ServiceProvider.GetRequiredService<Humans.Email.Data.EmailDbContext>();

        var sourceUser = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == sourceId, TestContext.Current.CancellationToken);
        sourceUser.Should().NotBeNull();
        sourceUser.MergedToUserId.Should().Be(targetId);
        sourceUser.MergedAt.Should().NotBeNull();
        sourceUser.LockoutEnabled.Should().BeTrue();
        sourceUser.LockoutEnd.Should().NotBeNull();
        sourceUser.LockoutEnd!.Value.Should().BeAfter(DateTimeOffset.UtcNow.AddYears(50));

        var targetUser = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == targetId, TestContext.Current.CancellationToken);
        targetUser.Should().NotBeNull();
        targetUser.MergedToUserId.Should().BeNull();
        targetUser.MergedAt.Should().BeNull();

        // Live cross-section tables hold nothing for the source.
        (await db.UserEmails.AsNoTracking().CountAsync(e => e.UserId == sourceId, TestContext.Current.CancellationToken))
            .Should().Be(0, "all UserEmails re-FK or collapse off the source");
        (await db.Set<IdentityUserLogin<Guid>>().AsNoTracking().CountAsync(l => l.UserId == sourceId, TestContext.Current.CancellationToken))
            .Should().Be(0, "all logins re-FK to target");
        (await db.CommunicationPreferences.AsNoTracking().CountAsync(cp => cp.UserId == sourceId, TestContext.Current.CancellationToken))
            .Should().Be(0);
        (await ticketsDb.EventParticipations.AsNoTracking().CountAsync(ep => ep.UserId == sourceId && ep.Status != null, TestContext.Current.CancellationToken))
            .Should().Be(0, "Tickets clears the source's participations; the legacy Users rows stay frozen");
        (await governanceDb.Applications.AsNoTracking().CountAsync(a => a.UserId == sourceId, TestContext.Current.CancellationToken))
            .Should().Be(0);
        (await authDb.RoleAssignments.AsNoTracking().CountAsync(ra =>
                ra.UserId == sourceId && (ra.RoleName == sharedRole || ra.RoleName == sourceOnlyRole), TestContext.Current.CancellationToken))
            .Should().Be(0);
        (await teamsDb.TeamMembers.AsNoTracking().CountAsync(tm =>
                tm.UserId == sourceId && tm.LeftAt == null
                && (tm.TeamId == sharedTeamId || tm.TeamId == sourceOnlyTeamId), TestContext.Current.CancellationToken))
            .Should().Be(0, "no active source memberships remain");
        (await teamsDb.TeamJoinRequests.AsNoTracking().CountAsync(r =>
                r.UserId == sourceId && r.TeamId == joinTeamId, TestContext.Current.CancellationToken))
            .Should().Be(0);
        (await notificationsDb.NotificationRecipients.AsNoTracking().CountAsync(nr =>
                nr.UserId == sourceId
                && (nr.NotificationId == sharedNotificationId || nr.NotificationId == sourceOnlyNotificationId), TestContext.Current.CancellationToken))
            .Should().Be(0);
        (await campaignsDb.CampaignGrants.AsNoTracking().CountAsync(g =>
                g.UserId == sourceId
                && (g.CampaignId == contestedCampaignId || g.CampaignId == sourceOnlyCampaignId), TestContext.Current.CancellationToken))
            .Should().Be(0);
        (await feedbackDb.FeedbackReports.AsNoTracking().CountAsync(r => r.UserId == sourceId, TestContext.Current.CancellationToken))
            .Should().Be(0);
        (await feedbackDb.FeedbackMessages.AsNoTracking().CountAsync(m => m.SenderUserId == sourceId, TestContext.Current.CancellationToken))
            .Should().Be(0);

        // Profile is the exception: the source row stays as an anonymized tombstone while its child rows re-FK.
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

        // Append-only sections keep the source rows for chain-follow.
        (await auditLogDb.AuditLogEntries.AsNoTracking()
                .CountAsync(a => a.Description == auditDescription && a.ActorUserId == sourceId, TestContext.Current.CancellationToken))
            .Should().Be(1, "audit log is append-only — source row stays for chain-follow");
        (await legalDb.ConsentRecords.AsNoTracking().CountAsync(c => c.UserId == sourceId, TestContext.Current.CancellationToken))
            .Should().BeGreaterThan(0, "consent records are append-only — source rows stay for chain-follow");
        (await budgetDb.BudgetAuditLogs.AsNoTracking()
                .CountAsync(b => b.Description == budgetDescription && b.ActorUserId == sourceId, TestContext.Current.CancellationToken))
            .Should().Be(1, "budget audit log is append-only — source row stays for chain-follow");

        // The target picked up the source's content, one assertion per fold rule.
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

        (await emailDb.CommunicationPreferences.AsNoTracking()
                .AnyAsync(cp => cp.UserId == targetId
                    && cp.Category == MessageCategory.VolunteerUpdates && cp.OptedOut == true, TestContext.Current.CancellationToken))
            .Should().BeTrue("source legacy CommPref folded into target's Email row");

        (await ticketsDb.EventParticipations.AsNoTracking()
                .AnyAsync(ep => ep.UserId == targetId && ep.Year == 2024
                    && ep.Status == ParticipationStatus.Attended, TestContext.Current.CancellationToken))
            .Should().BeTrue("source event participation folded into target by Tickets");

        (await governanceDb.Applications.AsNoTracking()
                .CountAsync(a => a.UserId == targetId
                    && a.MembershipTier == MembershipTier.Colaborador, TestContext.Current.CancellationToken))
            .Should().BeGreaterThan(0, "source application moved to target");

        (await authDb.RoleAssignments.AsNoTracking()
                .AnyAsync(ra => ra.UserId == targetId && ra.RoleName == sourceOnlyRole, TestContext.Current.CancellationToken))
            .Should().BeTrue("source-only role re-FK'd to target");
        (await authDb.RoleAssignments.AsNoTracking()
                .CountAsync(ra => ra.UserId == targetId && ra.RoleName == sharedRole, TestContext.Current.CancellationToken))
            .Should().Be(1, "shared role stays as target's single active assignment");

        (await teamsDb.TeamMembers.AsNoTracking()
                .AnyAsync(tm => tm.UserId == targetId && tm.TeamId == sourceOnlyTeamId
                    && tm.LeftAt == null, TestContext.Current.CancellationToken))
            .Should().BeTrue("target gained active membership on source-only team");
        (await teamsDb.TeamMembers.AsNoTracking()
                .CountAsync(tm => tm.UserId == targetId && tm.TeamId == sharedTeamId
                    && tm.LeftAt == null, TestContext.Current.CancellationToken))
            .Should().Be(1, "target keeps its single active membership on shared team");

        (await teamsDb.TeamJoinRequests.AsNoTracking()
                .AnyAsync(r => r.UserId == targetId && r.TeamId == joinTeamId, TestContext.Current.CancellationToken))
            .Should().BeTrue("source's team-join request re-FK'd to target");

        (await notificationsDb.NotificationRecipients.AsNoTracking()
                .AnyAsync(nr => nr.UserId == targetId
                    && nr.NotificationId == sourceOnlyNotificationId, TestContext.Current.CancellationToken))
            .Should().BeTrue("source-only notification recipient re-FK'd to target");
        (await notificationsDb.NotificationRecipients.AsNoTracking()
                .CountAsync(nr => nr.UserId == targetId
                    && nr.NotificationId == sharedNotificationId, TestContext.Current.CancellationToken))
            .Should().Be(1, "duplicate-on-shared-notification dropped, single row kept");

        (await campaignsDb.CampaignGrants.AsNoTracking()
                .AnyAsync(g => g.UserId == targetId && g.CampaignId == sourceOnlyCampaignId, TestContext.Current.CancellationToken))
            .Should().BeTrue("source-only campaign grant re-FK'd to target");
        (await campaignsDb.CampaignGrants.AsNoTracking()
                .CountAsync(g => g.UserId == targetId && g.CampaignId == contestedCampaignId, TestContext.Current.CancellationToken))
            .Should().Be(1, "contested campaign grant deduped to a single target row");

        (await feedbackDb.FeedbackReports.AsNoTracking()
                .AnyAsync(r => r.UserId == targetId && r.Description == $"src-report-{runTag}", TestContext.Current.CancellationToken))
            .Should().BeTrue("source feedback report re-FK'd to target");
        (await feedbackDb.FeedbackMessages.AsNoTracking()
                .AnyAsync(m => m.SenderUserId == targetId && m.Content == $"src-msg-{runTag}", TestContext.Current.CancellationToken))
            .Should().BeTrue("source feedback message re-FK'd to target");

        var sourceProfile = await db.Profiles.AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == sourceId, TestContext.Current.CancellationToken);
        sourceProfile.Should().NotBeNull("source profile row stays as a tombstone");
        sourceProfile.FirstName.Should().Be("Merged");
        sourceProfile.LastName.Should().Be("User");
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
                "Failed to seed admin user for AcceptAsyncFullFixtureTest: "
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
