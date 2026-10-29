using Humans.Email.Contracts;
using Humans.Governance.Contracts;
using Humans.Auth.Data;
using Humans.Auth.Domain;
using Humans.AuditLog.Data;
using Humans.Teams.Data;
using Humans.Notifications.Data;
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
using Humans.Users.Data.Repositories;
using Humans.AuditLog.Contracts;

namespace Humans.Integration.Tests.AccountMerge;

public class AcceptAsyncFoldTests(HumansTestDatabase database) : IntegrationTestBase(database)
{
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
                "Failed to seed admin user for AcceptAsyncFoldTests: "
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

    [HumansFact(Timeout = 30_000)]
    public async Task AcceptAsync_UserEmails_OrCombinesFlags_KeepsTargetPrimaryAndGoogle()
    {
        var sharedEmail = $"shared-{Guid.NewGuid():N}@example.com";
        var (sourceId, targetId) = await Factory.SeedMergeFixtureAsync(b =>
        {
            b.WithSourceEmail(sharedEmail, verified: true, isPrimary: false, isGoogle: false);
            b.WithTargetEmail(sharedEmail, verified: false, isPrimary: true, isGoogle: true);
        });
        var requestId = await Factory.SeedMergeRequestAsync(sourceId, targetId);

        var adminId = await SeedAdminUserAsync();
        await AcceptAsync(requestId, adminId);

        await using var assertScope = Factory.Services.CreateAsyncScope();
        var emailRepo = assertScope.ServiceProvider.GetRequiredService<IUserRepository>();

        var targetEmails = await emailRepo.GetUserEmailsByUserIdReadOnlyAsync(targetId, TestContext.Current.CancellationToken);
        var collapsed = targetEmails.Should()
            .ContainSingle(e => string.Equals(e.Email, sharedEmail, StringComparison.OrdinalIgnoreCase)).Subject;
        collapsed.IsVerified.Should().BeTrue("source's verified flag should OR-combine into the target row");
        collapsed.IsPrimary.Should().BeTrue("target's authoritative IsPrimary should be preserved");
        collapsed.IsGoogle.Should().BeTrue("target's authoritative IsGoogle should be preserved");

        var sourceEmails = await emailRepo.GetUserEmailsByUserIdReadOnlyAsync(sourceId, TestContext.Current.CancellationToken);
        sourceEmails.Should().NotContain(e => string.Equals(e.Email, sharedEmail, StringComparison.OrdinalIgnoreCase));
    }

    [HumansFact(Timeout = 30_000)]
    public async Task AcceptAsync_UserEmails_CollapsesSameEmail()
    {
        // The Email unique index covers verified rows only, so a pre-merge duplicate needs one side unverified.
        var collapseEmail = $"collapse-{Guid.NewGuid():N}@example.com";
        var sourceOnlyEmail = $"source-only-{Guid.NewGuid():N}@example.com";
        var (sourceId, targetId) = await Factory.SeedMergeFixtureAsync(b =>
        {
            b.WithSourceEmail(collapseEmail, verified: true);
            b.WithTargetEmail(collapseEmail, verified: false, isPrimary: true);
            b.WithSourceEmail(sourceOnlyEmail, verified: true);
        });
        var requestId = await Factory.SeedMergeRequestAsync(sourceId, targetId);

        var adminId = await SeedAdminUserAsync();
        await AcceptAsync(requestId, adminId);

        await using var assertScope = Factory.Services.CreateAsyncScope();
        var emailRepo = assertScope.ServiceProvider.GetRequiredService<IUserRepository>();

        var targetEmails = await emailRepo.GetUserEmailsByUserIdReadOnlyAsync(targetId, TestContext.Current.CancellationToken);

        targetEmails.Should().ContainSingle(e => string.Equals(e.Email, collapseEmail, StringComparison.OrdinalIgnoreCase));
        targetEmails.Should().ContainSingle(e => string.Equals(e.Email, sourceOnlyEmail, StringComparison.OrdinalIgnoreCase));

        var sourceEmails = await emailRepo.GetUserEmailsByUserIdReadOnlyAsync(sourceId, TestContext.Current.CancellationToken);
        sourceEmails.Should().BeEmpty();
    }

    [HumansFact(Timeout = 30_000)]
    public async Task AcceptAsync_AspNetUserLogins_ReFK()
    {
        // The PK is (LoginProvider, ProviderKey), so two users never share a key and nothing dedups: source logins
        // re-FK over. Each re-FK is a Remove+Add of one PK, which needs the two-pass save to dodge EF's identity map.
        var sourceKey1 = $"source-sub-1-{Guid.NewGuid():N}";
        var sourceKey2 = $"source-sub-2-{Guid.NewGuid():N}";
        var targetKey = $"target-sub-{Guid.NewGuid():N}";

        var (sourceId, targetId) = await Factory.SeedMergeFixtureAsync(b =>
        {
            b.WithSourceLogin("Google", sourceKey1);
            b.WithSourceLogin("Google", sourceKey2);
            b.WithTargetLogin("Google", targetKey);
        });
        var requestId = await Factory.SeedMergeRequestAsync(sourceId, targetId);

        var adminId = await SeedAdminUserAsync();
        await AcceptAsync(requestId, adminId);

        await using var assertScope = Factory.Services.CreateAsyncScope();
        var db = assertScope.ServiceProvider.GetRequiredService<UsersDbContext>();

        var targetLogins = await db.Set<IdentityUserLogin<Guid>>()
            .Where(l => l.UserId == targetId)
            .AsNoTracking()
            .ToListAsync(TestContext.Current.CancellationToken);

        targetLogins.Should().HaveCount(3);
        targetLogins.Should().ContainSingle(l =>
            string.Equals(l.LoginProvider, "Google", StringComparison.Ordinal)
            && string.Equals(l.ProviderKey, sourceKey1, StringComparison.Ordinal));
        targetLogins.Should().ContainSingle(l =>
            string.Equals(l.LoginProvider, "Google", StringComparison.Ordinal)
            && string.Equals(l.ProviderKey, sourceKey2, StringComparison.Ordinal));
        targetLogins.Should().ContainSingle(l =>
            string.Equals(l.LoginProvider, "Google", StringComparison.Ordinal)
            && string.Equals(l.ProviderKey, targetKey, StringComparison.Ordinal));

        var sourceLogins = await db.Set<IdentityUserLogin<Guid>>()
            .Where(l => l.UserId == sourceId)
            .AsNoTracking()
            .ToListAsync(TestContext.Current.CancellationToken);
        sourceLogins.Should().BeEmpty();
    }

    [HumansFact(Timeout = 30_000)]
    public async Task AcceptAsync_Profile_AnonymizesAndKeepsTombstoneRow()
    {
        var (sourceId, targetId) = await Factory.SeedMergeFixtureAsync();
        var requestId = await Factory.SeedMergeRequestAsync(sourceId, targetId);

        var adminId = await SeedAdminUserAsync();
        await AcceptAsync(requestId, adminId);

        await using var assertScope = Factory.Services.CreateAsyncScope();
        var db = assertScope.ServiceProvider.GetRequiredService<UsersDbContext>();

        // Source profile row still exists (tombstone) but with anonymized scalars.
        var sourceProfile = await db.Profiles
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == sourceId, TestContext.Current.CancellationToken);
        sourceProfile.Should().NotBeNull("source profile row is kept as a tombstone");
        sourceProfile.FirstName.Should().Be("Merged");
        sourceProfile.LastName.Should().Be("User");
        sourceProfile.BurnerName.Should().Be("Merged User");
        sourceProfile.Bio.Should().BeNull();

        var targetProfile = await db.Profiles
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == targetId, TestContext.Current.CancellationToken);
        targetProfile.Should().NotBeNull();
        targetProfile.FirstName.Should().Be("Target");
    }

    [HumansFact(Timeout = 30_000)]
    public async Task AcceptAsync_VolunteerHistory_Move_DedupIdenticalEntries()
    {
        var (sourceId, targetId) = await Factory.SeedMergeFixtureAsync(b =>
        {
            b.WithSourceVolunteerHistory(2024, "Nowhere 2024");
            b.WithTargetVolunteerHistory(2024, "Nowhere 2024"); // dup — drop source
            b.WithSourceVolunteerHistory(2023, "Build 2023");   // unique to source — moves
            b.WithTargetVolunteerHistory(2025, "Cleanup 2025"); // unique to target — stays
        });
        var requestId = await Factory.SeedMergeRequestAsync(sourceId, targetId);

        var adminId = await SeedAdminUserAsync();
        await AcceptAsync(requestId, adminId);

        await using var assertScope = Factory.Services.CreateAsyncScope();
        var db = assertScope.ServiceProvider.GetRequiredService<UsersDbContext>();

        var targetProfileId = await db.Profiles
            .AsNoTracking()
            .Where(p => p.UserId == targetId)
            .Select(p => p.Id)
            .SingleAsync(TestContext.Current.CancellationToken);

        var targetEntries = await db.VolunteerHistoryEntries
            .AsNoTracking()
            .Where(v => v.ProfileId == targetProfileId)
            .ToListAsync(TestContext.Current.CancellationToken);

        targetEntries.Should().HaveCount(3, "dedup keeps one of the dup pair plus the two unique entries");
        targetEntries.Should().ContainSingle(v => v.Date.Year == 2024 && v.EventName == "Nowhere 2024");
        targetEntries.Should().ContainSingle(v => v.Date.Year == 2023 && v.EventName == "Build 2023");
        targetEntries.Should().ContainSingle(v => v.Date.Year == 2025 && v.EventName == "Cleanup 2025");

        var sourceProfileId = await db.Profiles
            .AsNoTracking()
            .Where(p => p.UserId == sourceId)
            .Select(p => p.Id)
            .SingleAsync(TestContext.Current.CancellationToken);

        var sourceEntries = await db.VolunteerHistoryEntries
            .AsNoTracking()
            .Where(v => v.ProfileId == sourceProfileId)
            .ToListAsync(TestContext.Current.CancellationToken);
        sourceEntries.Should().BeEmpty();
    }

    [HumansFact(Timeout = 30_000)]
    public async Task AcceptAsync_Languages_Move_DedupKeepHighestProficiency()
    {
        var (sourceId, targetId) = await Factory.SeedMergeFixtureAsync(b =>
        {
            // Both have "es"; source's higher proficiency upgrades target's row.
            b.WithSourceLanguage("es", LanguageProficiency.Fluent);
            b.WithTargetLanguage("es", LanguageProficiency.Conversational);

            // Source-only: moves to target.
            b.WithSourceLanguage("fr", LanguageProficiency.Basic);

            // Target-only: stays.
            b.WithTargetLanguage("de", LanguageProficiency.Native);
        });
        var requestId = await Factory.SeedMergeRequestAsync(sourceId, targetId);

        var adminId = await SeedAdminUserAsync();
        await AcceptAsync(requestId, adminId);

        await using var assertScope = Factory.Services.CreateAsyncScope();
        var db = assertScope.ServiceProvider.GetRequiredService<UsersDbContext>();

        var targetProfileId = await db.Profiles
            .AsNoTracking()
            .Where(p => p.UserId == targetId)
            .Select(p => p.Id)
            .SingleAsync(TestContext.Current.CancellationToken);

        var targetLangs = await db.ProfileLanguages
            .AsNoTracking()
            .Where(l => l.ProfileId == targetProfileId)
            .ToListAsync(TestContext.Current.CancellationToken);

        targetLangs.Should().HaveCount(3);
        targetLangs.Single(l => string.Equals(l.LanguageCode, "es", StringComparison.Ordinal)).Proficiency
            .Should().Be(LanguageProficiency.Fluent, "higher proficiency wins on collision");
        targetLangs.Single(l => string.Equals(l.LanguageCode, "fr", StringComparison.Ordinal)).Proficiency
            .Should().Be(LanguageProficiency.Basic);
        targetLangs.Single(l => string.Equals(l.LanguageCode, "de", StringComparison.Ordinal)).Proficiency
            .Should().Be(LanguageProficiency.Native);
    }

    [HumansFact(Timeout = 30_000)]
    public async Task AcceptAsync_CommunicationPreferences_MostRecentWins()
    {
        var older = Instant.FromUtc(2025, 1, 1, 0, 0);
        var newer = Instant.FromUtc(2025, 6, 1, 0, 0);

        var (sourceId, targetId) = await Factory.SeedMergeFixtureAsync(b =>
        {
            // Source newer: its value is copied onto target.
            b.WithSourceCommPref(LegacyMessageCategory.VolunteerUpdates, optedOut: true, updatedAt: newer);
            b.WithTargetCommPref(LegacyMessageCategory.VolunteerUpdates, optedOut: false, updatedAt: older);

            // Target newer: target stands.
            b.WithSourceCommPref(LegacyMessageCategory.Marketing, optedOut: false, updatedAt: older);
            b.WithTargetCommPref(LegacyMessageCategory.Marketing, optedOut: true, updatedAt: newer);
        });
        var requestId = await Factory.SeedMergeRequestAsync(sourceId, targetId);

        var adminId = await SeedAdminUserAsync();
        await AcceptAsync(requestId, adminId);

        // The fold lands in Email's table; the source's legacy rows stay, shadowed by cleared rows so Email's fallback
        // can't resurrect them.
        await using var assertScope = Factory.Services.CreateAsyncScope();
        var db = assertScope.ServiceProvider.GetRequiredService<Humans.Email.Data.EmailDbContext>();

        var targetPrefs = await db.CommunicationPreferences
            .AsNoTracking()
            .Where(cp => cp.UserId == targetId)
            .ToListAsync(TestContext.Current.CancellationToken);

        targetPrefs.Should().HaveCount(2);
        targetPrefs.Single(cp => cp.Category == MessageCategory.VolunteerUpdates).OptedOut
            .Should().BeTrue("source was newer — its OptedOut=true should overwrite target");
        targetPrefs.Single(cp => cp.Category == MessageCategory.Marketing).OptedOut
            .Should().BeTrue("target was newer — target's OptedOut=true stands");

        var sourcePrefs = await db.CommunicationPreferences
            .AsNoTracking()
            .Where(cp => cp.UserId == sourceId)
            .ToListAsync(TestContext.Current.CancellationToken);
        sourcePrefs.Should().OnlyContain(cp => cp.OptedOut == null);
    }

    [HumansFact(Timeout = 30_000)]
    public async Task AcceptAsync_EventParticipation_HighestStatusWins_ByEnumPrecedence()
    {
        var (sourceId, targetId) = await Factory.SeedMergeFixtureAsync(b =>
        {
            // 2024: source Attended (highest precedence) beats target NotAttending.
            b.WithSourceEventParticipation(2024, LegacyParticipationStatus.Attended);
            b.WithTargetEventParticipation(2024, LegacyParticipationStatus.NotAttending);

            // 2025: source NotAttending loses to target Ticketed.
            b.WithSourceEventParticipation(2025, LegacyParticipationStatus.NotAttending);
            b.WithTargetEventParticipation(2025, LegacyParticipationStatus.Ticketed);

            // 2023: source-only, re-FKs to target.
            b.WithSourceEventParticipation(2023, LegacyParticipationStatus.Attended);
        });
        var requestId = await Factory.SeedMergeRequestAsync(sourceId, targetId);

        var adminId = await SeedAdminUserAsync();
        await AcceptAsync(requestId, adminId);

        await using var assertScope = Factory.Services.CreateAsyncScope();
        // Tickets owns the fold: both users' legacy rows are materialized into tickets_event_participations.
        var db = assertScope.ServiceProvider.GetRequiredService<TicketsDbContext>();

        var targetEvents = await db.EventParticipations
            .AsNoTracking()
            .Where(ep => ep.UserId == targetId)
            .ToListAsync(TestContext.Current.CancellationToken);

        targetEvents.Should().HaveCount(3);
        targetEvents.Single(ep => ep.Year == 2024).Status.Should().Be(ParticipationStatus.Attended);
        targetEvents.Single(ep => ep.Year == 2025).Status.Should().Be(ParticipationStatus.Ticketed);
        targetEvents.Single(ep => ep.Year == 2023).Status.Should().Be(ParticipationStatus.Attended);

        var sourceEvents = await db.EventParticipations
            .AsNoTracking()
            .Where(ep => ep.UserId == sourceId)
            .ToListAsync(TestContext.Current.CancellationToken);
        // The source's legacy rows stay frozen in Users; its cleared Tickets rows stop the fallback bringing them back.
        sourceEvents.Should().OnlyContain(ep => ep.Status == null);
    }

    [HumansFact(Timeout = 30_000)]
    public async Task AcceptAsync_Applications_Move_AllHistorical()
    {
        var (sourceId, targetId) = await Factory.SeedMergeFixtureAsync(b =>
        {
            // Every row moves; no dedup.
            b.WithSourceApplication();
            b.WithSourceApplication(MembershipTier.Asociado);
            b.WithTargetApplication();
        });
        var requestId = await Factory.SeedMergeRequestAsync(sourceId, targetId);

        var adminId = await SeedAdminUserAsync();
        await AcceptAsync(requestId, adminId);

        await using var assertScope = Factory.Services.CreateAsyncScope();
        var governanceDb = assertScope.ServiceProvider.GetRequiredService<GovernanceDbContext>();

        var targetApps = await governanceDb.Applications
            .AsNoTracking()
            .Where(a => a.UserId == targetId)
            .ToListAsync(TestContext.Current.CancellationToken);
        targetApps.Should().HaveCount(3);

        var sourceApps = await governanceDb.Applications
            .AsNoTracking()
            .Where(a => a.UserId == sourceId)
            .ToListAsync(TestContext.Current.CancellationToken);
        sourceApps.Should().BeEmpty();
    }

    [HumansFact(Timeout = 30_000)]
    public async Task AcceptAsync_FeedbackReportsAndMessages_Move()
    {
        var (sourceId, targetId) = await Factory.SeedMergeFixtureAsync(b =>
        {
            b.WithSourceFeedbackReport("Source bug A");
            b.WithSourceFeedbackReport("Source bug B");
            b.WithTargetFeedbackReport("Target bug C");
        });
        var requestId = await Factory.SeedMergeRequestAsync(sourceId, targetId);

        var adminId = await SeedAdminUserAsync();
        await AcceptAsync(requestId, adminId);

        await using var assertScope = Factory.Services.CreateAsyncScope();
        var feedbackDb = assertScope.ServiceProvider.GetRequiredService<FeedbackDbContext>();

        var targetReports = await feedbackDb.FeedbackReports
            .AsNoTracking()
            .Where(r => r.UserId == targetId)
            .ToListAsync(TestContext.Current.CancellationToken);
        targetReports.Should().HaveCount(3);
        targetReports.Should().ContainSingle(r => r.Description == "Source bug A");
        targetReports.Should().ContainSingle(r => r.Description == "Source bug B");
        targetReports.Should().ContainSingle(r => r.Description == "Target bug C");

        var sourceReports = await feedbackDb.FeedbackReports
            .AsNoTracking()
            .Where(r => r.UserId == sourceId)
            .ToListAsync(TestContext.Current.CancellationToken);
        sourceReports.Should().BeEmpty();
    }

    [HumansFact(Timeout = 30_000)]
    public async Task AcceptAsync_AuditLog_NotMutated_StaysAtSourceId()
    {
        var description = $"audit-source-action-{Guid.NewGuid():N}";

        var (sourceId, targetId) = await Factory.SeedMergeFixtureAsync(b =>
        {
            b.WithSourceAuditLogEntry("AccountAnonymized", description);
        });
        var requestId = await Factory.SeedMergeRequestAsync(sourceId, targetId);

        var adminId = await SeedAdminUserAsync();
        await AcceptAsync(requestId, adminId);

        await using var assertScope = Factory.Services.CreateAsyncScope();
        var auditLogDb = assertScope.ServiceProvider.GetRequiredService<AuditLogDbContext>();

        // The fold never mutates audit rows; chain-follow stitches them to the target at read time.
        var seededRows = await auditLogDb.AuditLogEntries
            .AsNoTracking()
            .Where(a => a.Description == description)
            .ToListAsync(TestContext.Current.CancellationToken);
        seededRows.Should().HaveCount(1);
        seededRows[0].ActorUserId.Should().Be(sourceId);
    }

    [HumansFact(Timeout = 30_000)]
    public async Task AcceptAsync_TombstonesSourceWithMergedToUserId()
    {
        var (sourceId, targetId) = await Factory.SeedMergeFixtureAsync();
        var requestId = await Factory.SeedMergeRequestAsync(sourceId, targetId);

        var adminId = await SeedAdminUserAsync();
        await AcceptAsync(requestId, adminId);

        await using var assertScope = Factory.Services.CreateAsyncScope();
        var db = assertScope.ServiceProvider.GetRequiredService<UsersDbContext>();

        var sourceUser = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == sourceId, TestContext.Current.CancellationToken);
        sourceUser.Should().NotBeNull();
        sourceUser.MergedToUserId.Should().Be(targetId);
        sourceUser.MergedAt.Should().NotBeNull();
        sourceUser.DisplayName.Should().Be("Merged User");
        // Legacy Identity email/username PII is scrubbed to a tombstone sentinel (GDPR).
        sourceUser.IdentityEmailColumn.Should().Be($"merged-{sourceId:N}@merged.local");
        sourceUser.UserName.Should().Be($"merged-{sourceId:N}@merged.local");
    }

    [HumansFact(Timeout = 30_000)]
    public async Task AcceptAsync_PreventsSourceLogin()
    {
        var (sourceId, targetId) = await Factory.SeedMergeFixtureAsync();
        var requestId = await Factory.SeedMergeRequestAsync(sourceId, targetId);

        var adminId = await SeedAdminUserAsync();
        await AcceptAsync(requestId, adminId);

        await using var assertScope = Factory.Services.CreateAsyncScope();
        var db = assertScope.ServiceProvider.GetRequiredService<UsersDbContext>();

        var sourceUser = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == sourceId, TestContext.Current.CancellationToken);
        sourceUser.Should().NotBeNull();
        sourceUser.LockoutEnabled.Should().BeTrue();
        sourceUser.LockoutEnd.Should().NotBeNull("source must be locked out forever after merge");
        sourceUser.LockoutEnd!.Value.Should().BeCloseTo(DateTimeOffset.MaxValue, TimeSpan.FromDays(1));
    }

    [HumansFact(Timeout = 30_000)]
    public async Task AcceptAsync_ContactFields_Move_DedupOnTypeValue()
    {
        var (sourceId, targetId) = await Factory.SeedMergeFixtureAsync(b =>
        {
            b.WithTargetContactField(ContactFieldType.Phone, "+34 600 100 200");
            b.WithSourceContactField(ContactFieldType.Phone, "+34 600 100 200"); // dup — drop source
            b.WithSourceContactField(ContactFieldType.Telegram, "@source-handle"); // unique to source — moves
            b.WithTargetContactField(ContactFieldType.Telegram, "@target-handle"); // unique to target — stays
        });
        var requestId = await Factory.SeedMergeRequestAsync(sourceId, targetId);

        var adminId = await SeedAdminUserAsync();
        await AcceptAsync(requestId, adminId);

        await using var assertScope = Factory.Services.CreateAsyncScope();
        var db = assertScope.ServiceProvider.GetRequiredService<UsersDbContext>();

        var targetProfileId = await db.Profiles
            .AsNoTracking()
            .Where(p => p.UserId == targetId)
            .Select(p => p.Id)
            .SingleAsync(TestContext.Current.CancellationToken);

        var targetFields = await db.ContactFields
            .AsNoTracking()
            .Where(cf => cf.ProfileId == targetProfileId)
            .ToListAsync(TestContext.Current.CancellationToken);

        // Target's two rows plus the source-only Telegram; the source's duplicate Phone was dropped.
        targetFields.Should().HaveCount(3);
        targetFields.Should().ContainSingle(cf => cf.FieldType == ContactFieldType.Phone && cf.Value == "+34 600 100 200");
        targetFields.Should().ContainSingle(cf => cf.FieldType == ContactFieldType.Telegram && cf.Value == "@target-handle");
        targetFields.Should().ContainSingle(cf => cf.FieldType == ContactFieldType.Telegram && cf.Value == "@source-handle");

        var sourceProfileId = await db.Profiles
            .AsNoTracking()
            .Where(p => p.UserId == sourceId)
            .Select(p => p.Id)
            .SingleAsync(TestContext.Current.CancellationToken);

        var sourceFields = await db.ContactFields
            .AsNoTracking()
            .Where(cf => cf.ProfileId == sourceProfileId)
            .ToListAsync(TestContext.Current.CancellationToken);
        sourceFields.Should().BeEmpty();
    }

    [HumansFact(Timeout = 30_000)]
    public async Task AcceptAsync_RoleAssignments_ReFKs_DropsSameKey()
    {
        var sharedRole = $"shared-role-{Guid.NewGuid():N}";
        var sourceOnlyRole = $"source-only-role-{Guid.NewGuid():N}";

        var (sourceId, targetId) = await Factory.SeedMergeFixtureAsync(b =>
        {
            // Both active on sharedRole: source's row drops.
            b.WithSourceRoleAssignment(sharedRole);
            b.WithTargetRoleAssignment(sharedRole);

            // Source-only: re-FKs to target.
            b.WithSourceRoleAssignment(sourceOnlyRole);
        });
        var requestId = await Factory.SeedMergeRequestAsync(sourceId, targetId);

        var adminId = await SeedAdminUserAsync();
        await AcceptAsync(requestId, adminId);

        await using var assertScope = Factory.Services.CreateAsyncScope();
        var db = assertScope.ServiceProvider.GetRequiredService<UsersDbContext>();
        var authDb = assertScope.ServiceProvider.GetRequiredService<AuthDbContext>();

        var targetRows = await authDb.RoleAssignments
            .AsNoTracking()
            .Where(ra => ra.UserId == targetId
                && (ra.RoleName == sharedRole || ra.RoleName == sourceOnlyRole))
            .ToListAsync(TestContext.Current.CancellationToken);

        targetRows.Should().HaveCount(2);
        targetRows.Should().ContainSingle(ra => string.Equals(ra.RoleName, sharedRole, StringComparison.Ordinal));
        targetRows.Should().ContainSingle(ra => string.Equals(ra.RoleName, sourceOnlyRole, StringComparison.Ordinal));

        var sourceRows = await authDb.RoleAssignments
            .AsNoTracking()
            .Where(ra => ra.UserId == sourceId
                && (ra.RoleName == sharedRole || ra.RoleName == sourceOnlyRole))
            .ToListAsync(TestContext.Current.CancellationToken);
        sourceRows.Should().BeEmpty();
    }

    [HumansFact(Timeout = 30_000)]
    public async Task AcceptAsync_TeamMembers_AddTargetRemoveSource_NonSystemOnly()
    {
        Guid sharedTeamId = Guid.Empty, sourceOnlyTeamId = Guid.Empty;

        var (sourceId, targetId) = await Factory.SeedMergeFixtureAsync(b =>
        {
            sharedTeamId = b.SeedTeamNow($"Shared-{Guid.NewGuid():N}");
            sourceOnlyTeamId = b.SeedTeamNow($"SourceOnly-{Guid.NewGuid():N}");

            // Both on sharedTeam: source's slot drops.
            b.WithSourceTeamMember(sharedTeamId);
            b.WithTargetTeamMember(sharedTeamId);

            // Source-only team: target is added, source removed.
            b.WithSourceTeamMember(sourceOnlyTeamId);
        });
        var requestId = await Factory.SeedMergeRequestAsync(sourceId, targetId);

        var adminId = await SeedAdminUserAsync();
        await AcceptAsync(requestId, adminId);

        await using var assertScope = Factory.Services.CreateAsyncScope();
        var db = assertScope.ServiceProvider.GetRequiredService<UsersDbContext>();
        var teamsDb = assertScope.ServiceProvider.GetRequiredService<TeamsDbContext>();

        var targetActiveTeams = await teamsDb.TeamMembers
            .AsNoTracking()
            .Where(tm => tm.UserId == targetId && tm.LeftAt == null
                && (tm.TeamId == sharedTeamId || tm.TeamId == sourceOnlyTeamId))
            .Select(tm => tm.TeamId)
            .ToListAsync(TestContext.Current.CancellationToken);
        targetActiveTeams.Should().Contain(sharedTeamId);
        targetActiveTeams.Should().Contain(sourceOnlyTeamId);

        var sourceActive = await teamsDb.TeamMembers
            .AsNoTracking()
            .Where(tm => tm.UserId == sourceId && tm.LeftAt == null
                && (tm.TeamId == sharedTeamId || tm.TeamId == sourceOnlyTeamId))
            .ToListAsync(TestContext.Current.CancellationToken);
        sourceActive.Should().BeEmpty();
    }

    [HumansFact(Timeout = 30_000)]
    public async Task AcceptAsync_TeamJoinRequests_Move_DropDuplicateActive()
    {
        Guid contestedTeamId = Guid.Empty, sourceOnlyTeamId = Guid.Empty;

        var (sourceId, targetId) = await Factory.SeedMergeFixtureAsync(b =>
        {
            contestedTeamId = b.SeedTeamNow($"Contested-{Guid.NewGuid():N}");
            sourceOnlyTeamId = b.SeedTeamNow($"SourceOnlyTJR-{Guid.NewGuid():N}");

            // Both pending on contestedTeam: source's drops.
            b.WithSourceTeamJoinRequest(contestedTeamId);
            b.WithTargetTeamJoinRequest(contestedTeamId);

            // Source-only: re-FKs to target.
            b.WithSourceTeamJoinRequest(sourceOnlyTeamId);
        });
        var requestId = await Factory.SeedMergeRequestAsync(sourceId, targetId);

        var adminId = await SeedAdminUserAsync();
        await AcceptAsync(requestId, adminId);

        await using var assertScope = Factory.Services.CreateAsyncScope();
        var db = assertScope.ServiceProvider.GetRequiredService<UsersDbContext>();
        var teamsDb = assertScope.ServiceProvider.GetRequiredService<TeamsDbContext>();

        var targetRequests = await teamsDb.TeamJoinRequests
            .AsNoTracking()
            .Where(r => r.UserId == targetId
                && (r.TeamId == contestedTeamId || r.TeamId == sourceOnlyTeamId))
            .ToListAsync(TestContext.Current.CancellationToken);

        targetRequests.Should().HaveCount(2);
        targetRequests.Should().ContainSingle(r => r.TeamId == contestedTeamId);
        targetRequests.Should().ContainSingle(r => r.TeamId == sourceOnlyTeamId);

        var sourceRequests = await teamsDb.TeamJoinRequests
            .AsNoTracking()
            .Where(r => r.UserId == sourceId
                && (r.TeamId == contestedTeamId || r.TeamId == sourceOnlyTeamId))
            .ToListAsync(TestContext.Current.CancellationToken);
        sourceRequests.Should().BeEmpty();
    }

    [HumansFact(Timeout = 30_000)]
    public async Task AcceptAsync_NotificationRecipients_Move_DropDuplicate()
    {
        Guid sharedNotificationId = Guid.Empty, sourceOnlyNotificationId = Guid.Empty;

        var (sourceId, targetId) = await Factory.SeedMergeFixtureAsync(b =>
        {
            sharedNotificationId = b.SeedNotificationNow($"shared-{Guid.NewGuid():N}");
            sourceOnlyNotificationId = b.SeedNotificationNow($"source-only-{Guid.NewGuid():N}");

            b.WithSourceNotificationRecipient(sharedNotificationId);
            b.WithTargetNotificationRecipient(sharedNotificationId);
            b.WithSourceNotificationRecipient(sourceOnlyNotificationId);
        });
        var requestId = await Factory.SeedMergeRequestAsync(sourceId, targetId);

        var adminId = await SeedAdminUserAsync();
        await AcceptAsync(requestId, adminId);

        await using var assertScope = Factory.Services.CreateAsyncScope();
        var db = assertScope.ServiceProvider.GetRequiredService<UsersDbContext>();
        var notificationsDb = assertScope.ServiceProvider.GetRequiredService<NotificationsDbContext>();

        var targetRecipients = await notificationsDb.NotificationRecipients
            .AsNoTracking()
            .Where(nr => nr.UserId == targetId
                && (nr.NotificationId == sharedNotificationId || nr.NotificationId == sourceOnlyNotificationId))
            .ToListAsync(TestContext.Current.CancellationToken);

        targetRecipients.Should().HaveCount(2, "duplicate on shared notification dropped, source-only re-FK'd");
        targetRecipients.Should().ContainSingle(nr => nr.NotificationId == sharedNotificationId);
        targetRecipients.Should().ContainSingle(nr => nr.NotificationId == sourceOnlyNotificationId);

        var sourceRecipients = await notificationsDb.NotificationRecipients
            .AsNoTracking()
            .Where(nr => nr.UserId == sourceId
                && (nr.NotificationId == sharedNotificationId || nr.NotificationId == sourceOnlyNotificationId))
            .ToListAsync(TestContext.Current.CancellationToken);
        sourceRecipients.Should().BeEmpty();
    }

    [HumansFact(Timeout = 30_000)]
    public async Task AcceptAsync_CampaignGrants_Move_DedupPerCampaign()
    {
        Guid contestedCampaignId, sourceOnlyCampaignId;

        var (sourceId, targetId) = await Factory.SeedMergeFixtureAsync();

        // Campaign.CreatedByUserId needs a real user; the fold doesn't touch Campaigns.
        var creatorId = sourceId;

        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var builder = new MergeFixtureBuilder(scope, sourceId, targetId);
            contestedCampaignId = builder.SeedCampaignNow($"Contested-{Guid.NewGuid():N}", creatorId);
            sourceOnlyCampaignId = builder.SeedCampaignNow($"SourceOnly-{Guid.NewGuid():N}", creatorId);

            builder
                .WithSourceCampaignGrant(contestedCampaignId)
                .WithTargetCampaignGrant(contestedCampaignId)
                .WithSourceCampaignGrant(sourceOnlyCampaignId);

            await builder.SaveAllAsync();
        }
        var requestId = await Factory.SeedMergeRequestAsync(sourceId, targetId);

        var adminId = await SeedAdminUserAsync();
        await AcceptAsync(requestId, adminId);

        await using var assertScope = Factory.Services.CreateAsyncScope();
        var campaignsDb = assertScope.ServiceProvider.GetRequiredService<CampaignsDbContext>();

        var targetGrants = await campaignsDb.CampaignGrants
            .AsNoTracking()
            .Where(g => g.UserId == targetId
                && (g.CampaignId == contestedCampaignId || g.CampaignId == sourceOnlyCampaignId))
            .ToListAsync(TestContext.Current.CancellationToken);
        targetGrants.Should().HaveCount(2);
        targetGrants.Should().ContainSingle(g => g.CampaignId == contestedCampaignId);
        targetGrants.Should().ContainSingle(g => g.CampaignId == sourceOnlyCampaignId);

        var sourceGrants = await campaignsDb.CampaignGrants
            .AsNoTracking()
            .Where(g => g.UserId == sourceId
                && (g.CampaignId == contestedCampaignId || g.CampaignId == sourceOnlyCampaignId))
            .ToListAsync(TestContext.Current.CancellationToken);
        sourceGrants.Should().BeEmpty();
    }

    [HumansFact(Timeout = 30_000)]
    public async Task AcceptAsync_FeedbackMessages_Move()
    {
        Guid reportId;
        var sourceContent = $"source-msg-{Guid.NewGuid():N}";
        var targetContent = $"target-msg-{Guid.NewGuid():N}";

        var (sourceId, targetId) = await Factory.SeedMergeFixtureAsync();

        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var builder = new MergeFixtureBuilder(scope, sourceId, targetId);
            // The target's report, so only the message's SenderUserId moves.
            reportId = builder.SeedFeedbackReportNow(targetId, $"report-{Guid.NewGuid():N}");

            builder
                .WithSourceFeedbackMessage(reportId, sourceContent)
                .WithTargetFeedbackMessage(reportId, targetContent);

            await builder.SaveAllAsync();
        }
        var requestId = await Factory.SeedMergeRequestAsync(sourceId, targetId);

        var adminId = await SeedAdminUserAsync();
        await AcceptAsync(requestId, adminId);

        await using var assertScope = Factory.Services.CreateAsyncScope();
        var feedbackDb = assertScope.ServiceProvider.GetRequiredService<FeedbackDbContext>();

        var targetMessages = await feedbackDb.FeedbackMessages
            .AsNoTracking()
            .Where(m => m.SenderUserId == targetId
                && (m.Content == sourceContent || m.Content == targetContent))
            .ToListAsync(TestContext.Current.CancellationToken);
        targetMessages.Should().HaveCount(2);

        var sourceMessages = await feedbackDb.FeedbackMessages
            .AsNoTracking()
            .Where(m => m.SenderUserId == sourceId)
            .ToListAsync(TestContext.Current.CancellationToken);
        sourceMessages.Should().BeEmpty();
    }

    [HumansFact(Timeout = 30_000)]
    public async Task AcceptAsync_BudgetAuditLog_NotMutated_StaysAtSourceId()
    {
        var description = $"budget-source-action-{Guid.NewGuid():N}";

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

        await using var assertScope = Factory.Services.CreateAsyncScope();
        var budgetDb = assertScope.ServiceProvider.GetRequiredService<BudgetDbContext>();

        // Append-only: the fold never mutates it; chain-follow stitches at read time.
        var rows = await budgetDb.BudgetAuditLogs
            .AsNoTracking()
            .Where(l => l.Description == description)
            .ToListAsync(TestContext.Current.CancellationToken);
        rows.Should().HaveCount(1);
        rows[0].ActorUserId.Should().Be(sourceId);
    }

    [HumansFact(Timeout = 60_000)]
    public async Task AcceptAsync_HonoursAdminChosenSurvivor_OverRequestDirection()
    {
        // The request is Target=<new account>, Source=<primary>; keeping the Source must override the stored direction.
        var (requestId, requestTargetId, requestSourceId) =
            await Factory.SeedPendingMergeRequestAsync($"shared-{Guid.NewGuid():N}@example.com");

        var adminId = await SeedAdminUserAsync();
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var sut = scope.ServiceProvider.GetRequiredService<IAccountMergeService>();
            await sut.AcceptAsync(requestId, adminId, survivorUserId: requestSourceId, ct: TestContext.Current.CancellationToken); // flip: keep the Source
        }

        await using var assertScope = Factory.Services.CreateAsyncScope();
        var db = assertScope.ServiceProvider.GetRequiredService<UsersDbContext>();
        (await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == requestSourceId, TestContext.Current.CancellationToken))!.MergedToUserId
            .Should().BeNull("the admin-chosen survivor is never tombstoned");
        (await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == requestTargetId, TestContext.Current.CancellationToken))!.MergedToUserId
            .Should().Be(requestSourceId, "the request target was archived into the chosen survivor");
        (await db.AccountMergeRequests.AsNoTracking().FirstOrDefaultAsync(r => r.Id == requestId, TestContext.Current.CancellationToken))!.Status
            .Should().Be(AccountMergeRequestStatus.Accepted);
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
