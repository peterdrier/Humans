using AwesomeAssertions;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Repositories;
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
/// Phase 6.2 of the AccountMergeService fold-into-target redesign:
/// per-rule integration tests that exercise <c>AcceptAsync</c> end-to-end.
/// Each method seeds source/target users plus per-section data via
/// <see cref="MergeFixtureBuilder"/>, calls <c>AcceptAsync</c>, then asserts
/// the resulting fold against the rule documented in the fold-redesign plan.
/// </summary>
public class AcceptAsyncFoldTests : IClassFixture<HumansWebApplicationFactory>
{
    private readonly HumansWebApplicationFactory _factory;

    public AcceptAsyncFoldTests(HumansWebApplicationFactory factory) => _factory = factory;

    private async Task<Guid> SeedAdminUserAsync()
    {
        // AcceptAsync writes ResolvedByUserId = adminUserId on the merge request
        // and ActorUserId = adminUserId on the audit row, both FK'd to AspNetUsers.
        // It also calls TeamService.RemoveMemberAsync for non-system team folds,
        // which requires the actor to be Admin / Board / TeamsAdmin (per
        // CanUserApproveRequestsForTeamAsync). Seed an admin User + an active
        // Admin RoleAssignment per test so those FKs resolve and authorization
        // succeeds.
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
                "Failed to seed admin user for AcceptAsyncFoldTests: "
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

    // ==================================================================
    // UserEmail — rules 1 & 2 of the fold spec
    // ==================================================================

    [HumansFact(Timeout = 30_000)]
    public async Task AcceptAsync_UserEmails_OrCombinesFlags_KeepsTargetPrimaryAndGoogle()
    {
        // Per-test unique address so the user_emails Email-uniqueness index
        // doesn't trip when the class fixture's shared DB carries rows from
        // an earlier test in the same run.
        var sharedEmail = $"shared-{Guid.NewGuid():N}@example.com";
        var (sourceId, targetId) = await _factory.SeedMergeFixtureAsync(b =>
        {
            b.WithSourceEmail(sharedEmail, verified: true, isPrimary: false, isGoogle: false);
            b.WithTargetEmail(sharedEmail, verified: false, isPrimary: true, isGoogle: true);
        });
        var requestId = await _factory.SeedMergeRequestAsync(sourceId, targetId);

        var adminId = await SeedAdminUserAsync();
        await AcceptAsync(requestId, adminId);

        await using var assertScope = _factory.Services.CreateAsyncScope();
        var emailRepo = assertScope.ServiceProvider.GetRequiredService<IUserEmailRepository>();

        var targetEmails = await emailRepo.GetByUserIdReadOnlyAsync(targetId);
        var collapsed = targetEmails.Should()
            .ContainSingle(e => string.Equals(e.Email, sharedEmail, StringComparison.OrdinalIgnoreCase)).Subject;
        collapsed.IsVerified.Should().BeTrue("source's verified flag should OR-combine into the target row");
        collapsed.IsPrimary.Should().BeTrue("target's authoritative IsPrimary should be preserved");
        collapsed.IsGoogle.Should().BeTrue("target's authoritative IsGoogle should be preserved");

        var sourceEmails = await emailRepo.GetByUserIdReadOnlyAsync(sourceId);
        sourceEmails.Should().NotContain(e => string.Equals(e.Email, sharedEmail, StringComparison.OrdinalIgnoreCase));
    }

    [HumansFact(Timeout = 30_000)]
    public async Task AcceptAsync_UserEmails_CollapsesSameEmail()
    {
        // The unique index on UserEmail.Email is filtered to verified=true,
        // so the duplicate-pre-merge scenario is only legal when at most one
        // side is verified. Source verified, target unverified — fold should
        // OR-combine into a single verified target row.
        var collapseEmail = $"collapse-{Guid.NewGuid():N}@example.com";
        var sourceOnlyEmail = $"source-only-{Guid.NewGuid():N}@example.com";
        var (sourceId, targetId) = await _factory.SeedMergeFixtureAsync(b =>
        {
            b.WithSourceEmail(collapseEmail, verified: true);
            b.WithTargetEmail(collapseEmail, verified: false, isPrimary: true);
            b.WithSourceEmail(sourceOnlyEmail, verified: true);
        });
        var requestId = await _factory.SeedMergeRequestAsync(sourceId, targetId);

        var adminId = await SeedAdminUserAsync();
        await AcceptAsync(requestId, adminId);

        await using var assertScope = _factory.Services.CreateAsyncScope();
        var emailRepo = assertScope.ServiceProvider.GetRequiredService<IUserEmailRepository>();

        var targetEmails = await emailRepo.GetByUserIdReadOnlyAsync(targetId);

        // Same address collapses to a single target row.
        targetEmails.Should().ContainSingle(e => string.Equals(e.Email, collapseEmail, StringComparison.OrdinalIgnoreCase));
        // Source-only address re-FK'd onto target.
        targetEmails.Should().ContainSingle(e => string.Equals(e.Email, sourceOnlyEmail, StringComparison.OrdinalIgnoreCase));

        var sourceEmails = await emailRepo.GetByUserIdReadOnlyAsync(sourceId);
        sourceEmails.Should().BeEmpty();
    }

    // ==================================================================
    // AspNetUserLogins — rule 3
    // ==================================================================

    [HumansFact(Timeout = 30_000)]
    public async Task AcceptAsync_AspNetUserLogins_ReFK()
    {
        // user_logins PK is (LoginProvider, ProviderKey) — UserId is just an
        // FK — so two users can never share the same (provider, key) in the
        // DB and no dedup is possible. The realistic merge scenario is:
        // source has logins with keys that are NOT on target, and they
        // re-FK over. The code path also hits the EF identity-map conflict
        // (Remove+Add same composite PK in one DbContext) on every re-FK,
        // which the two-pass Remove-SaveChanges-Add structure fixes.
        var sourceKey1 = $"source-sub-1-{Guid.NewGuid():N}";
        var sourceKey2 = $"source-sub-2-{Guid.NewGuid():N}";
        var targetKey = $"target-sub-{Guid.NewGuid():N}";

        var (sourceId, targetId) = await _factory.SeedMergeFixtureAsync(b =>
        {
            b.WithSourceLogin("Google", sourceKey1);
            b.WithSourceLogin("Google", sourceKey2);
            b.WithTargetLogin("Google", targetKey);
        });
        var requestId = await _factory.SeedMergeRequestAsync(sourceId, targetId);

        var adminId = await SeedAdminUserAsync();
        await AcceptAsync(requestId, adminId);

        await using var assertScope = _factory.Services.CreateAsyncScope();
        var db = assertScope.ServiceProvider.GetRequiredService<HumansDbContext>();

        var targetLogins = await db.Set<IdentityUserLogin<Guid>>()
            .Where(l => l.UserId == targetId)
            .AsNoTracking()
            .ToListAsync();

        // All three logins now point at target.
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
            .ToListAsync();
        sourceLogins.Should().BeEmpty();
    }

    // ==================================================================
    // Profile — rule 4
    // ==================================================================

    [HumansFact(Timeout = 30_000)]
    public async Task AcceptAsync_Profile_AnonymizesAndKeepsTombstoneRow()
    {
        var (sourceId, targetId) = await _factory.SeedMergeFixtureAsync();
        var requestId = await _factory.SeedMergeRequestAsync(sourceId, targetId);

        var adminId = await SeedAdminUserAsync();
        await AcceptAsync(requestId, adminId);

        await using var assertScope = _factory.Services.CreateAsyncScope();
        var db = assertScope.ServiceProvider.GetRequiredService<HumansDbContext>();

        // Source profile row still exists (tombstone) but with anonymized scalars.
        var sourceProfile = await db.Profiles
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == sourceId);
        sourceProfile.Should().NotBeNull("source profile row is kept as a tombstone");
        sourceProfile!.FirstName.Should().Be("Merged");
        sourceProfile.LastName.Should().Be("User");
        sourceProfile.BurnerName.Should().Be(string.Empty);
        sourceProfile.Bio.Should().BeNull();

        // Target profile is untouched.
        var targetProfile = await db.Profiles
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == targetId);
        targetProfile.Should().NotBeNull();
        targetProfile!.FirstName.Should().Be("Target");
    }

    // ==================================================================
    // VolunteerHistory + Languages — rules 6, 7
    // ==================================================================

    [HumansFact(Timeout = 30_000)]
    public async Task AcceptAsync_VolunteerHistory_Move_DedupIdenticalEntries()
    {
        var (sourceId, targetId) = await _factory.SeedMergeFixtureAsync(b =>
        {
            b.WithSourceVolunteerHistory(2024, "Nowhere 2024");
            b.WithTargetVolunteerHistory(2024, "Nowhere 2024"); // dup — drop source
            b.WithSourceVolunteerHistory(2023, "Build 2023");   // unique to source — moves
            b.WithTargetVolunteerHistory(2025, "Cleanup 2025"); // unique to target — stays
        });
        var requestId = await _factory.SeedMergeRequestAsync(sourceId, targetId);

        var adminId = await SeedAdminUserAsync();
        await AcceptAsync(requestId, adminId);

        await using var assertScope = _factory.Services.CreateAsyncScope();
        var db = assertScope.ServiceProvider.GetRequiredService<HumansDbContext>();

        var targetProfileId = await db.Profiles
            .AsNoTracking()
            .Where(p => p.UserId == targetId)
            .Select(p => p.Id)
            .SingleAsync();

        var targetEntries = await db.VolunteerHistoryEntries
            .AsNoTracking()
            .Where(v => v.ProfileId == targetProfileId)
            .ToListAsync();

        targetEntries.Should().HaveCount(3, "dedup keeps one of the dup pair plus the two unique entries");
        targetEntries.Should().ContainSingle(v => v.Date.Year == 2024 && v.EventName == "Nowhere 2024");
        targetEntries.Should().ContainSingle(v => v.Date.Year == 2023 && v.EventName == "Build 2023");
        targetEntries.Should().ContainSingle(v => v.Date.Year == 2025 && v.EventName == "Cleanup 2025");

        var sourceProfileId = await db.Profiles
            .AsNoTracking()
            .Where(p => p.UserId == sourceId)
            .Select(p => p.Id)
            .SingleAsync();

        var sourceEntries = await db.VolunteerHistoryEntries
            .AsNoTracking()
            .Where(v => v.ProfileId == sourceProfileId)
            .ToListAsync();
        sourceEntries.Should().BeEmpty();
    }

    [HumansFact(Timeout = 30_000)]
    public async Task AcceptAsync_Languages_Move_DedupKeepHighestProficiency()
    {
        var (sourceId, targetId) = await _factory.SeedMergeFixtureAsync(b =>
        {
            // Both have "es" — source higher proficiency, target's row should
            // be upgraded.
            b.WithSourceLanguage("es", LanguageProficiency.Fluent);
            b.WithTargetLanguage("es", LanguageProficiency.Conversational);

            // Source-only "fr" moves to target.
            b.WithSourceLanguage("fr", LanguageProficiency.Basic);

            // Target-only "de" stays.
            b.WithTargetLanguage("de", LanguageProficiency.Native);
        });
        var requestId = await _factory.SeedMergeRequestAsync(sourceId, targetId);

        var adminId = await SeedAdminUserAsync();
        await AcceptAsync(requestId, adminId);

        await using var assertScope = _factory.Services.CreateAsyncScope();
        var db = assertScope.ServiceProvider.GetRequiredService<HumansDbContext>();

        var targetProfileId = await db.Profiles
            .AsNoTracking()
            .Where(p => p.UserId == targetId)
            .Select(p => p.Id)
            .SingleAsync();

        var targetLangs = await db.ProfileLanguages
            .AsNoTracking()
            .Where(l => l.ProfileId == targetProfileId)
            .ToListAsync();

        targetLangs.Should().HaveCount(3);
        targetLangs.Single(l => string.Equals(l.LanguageCode, "es", StringComparison.Ordinal)).Proficiency
            .Should().Be(LanguageProficiency.Fluent, "higher proficiency wins on collision");
        targetLangs.Single(l => string.Equals(l.LanguageCode, "fr", StringComparison.Ordinal)).Proficiency
            .Should().Be(LanguageProficiency.Basic);
        targetLangs.Single(l => string.Equals(l.LanguageCode, "de", StringComparison.Ordinal)).Proficiency
            .Should().Be(LanguageProficiency.Native);
    }

    // ==================================================================
    // CommunicationPreferences — rule 8
    // ==================================================================

    [HumansFact(Timeout = 30_000)]
    public async Task AcceptAsync_CommunicationPreferences_MostRecentWins()
    {
        var older = Instant.FromUtc(2025, 1, 1, 0, 0);
        var newer = Instant.FromUtc(2025, 6, 1, 0, 0);

        var (sourceId, targetId) = await _factory.SeedMergeFixtureAsync(b =>
        {
            // Same category — source is newer, so source's OptedOut value wins
            // and gets copied onto target before the source row is deleted.
            b.WithSourceCommPref(MessageCategory.VolunteerUpdates, optedOut: true, updatedAt: newer);
            b.WithTargetCommPref(MessageCategory.VolunteerUpdates, optedOut: false, updatedAt: older);

            // Same category — target newer, target value stands.
            b.WithSourceCommPref(MessageCategory.Marketing, optedOut: false, updatedAt: older);
            b.WithTargetCommPref(MessageCategory.Marketing, optedOut: true, updatedAt: newer);
        });
        var requestId = await _factory.SeedMergeRequestAsync(sourceId, targetId);

        var adminId = await SeedAdminUserAsync();
        await AcceptAsync(requestId, adminId);

        await using var assertScope = _factory.Services.CreateAsyncScope();
        var db = assertScope.ServiceProvider.GetRequiredService<HumansDbContext>();

        var targetPrefs = await db.CommunicationPreferences
            .AsNoTracking()
            .Where(cp => cp.UserId == targetId)
            .ToListAsync();

        targetPrefs.Should().HaveCount(2);
        targetPrefs.Single(cp => cp.Category == MessageCategory.VolunteerUpdates).OptedOut
            .Should().BeTrue("source was newer — its OptedOut=true should overwrite target");
        targetPrefs.Single(cp => cp.Category == MessageCategory.Marketing).OptedOut
            .Should().BeTrue("target was newer — target's OptedOut=true stands");

        var sourcePrefs = await db.CommunicationPreferences
            .AsNoTracking()
            .Where(cp => cp.UserId == sourceId)
            .ToListAsync();
        sourcePrefs.Should().BeEmpty();
    }

    // ==================================================================
    // EventParticipation — rule 9
    // ==================================================================

    [HumansFact(Timeout = 30_000)]
    public async Task AcceptAsync_EventParticipation_HighestStatusWins_ByEnumPrecedence()
    {
        var (sourceId, targetId) = await _factory.SeedMergeFixtureAsync(b =>
        {
            // Year 2024 — source Attended (highest precedence) beats target NotAttending.
            b.WithSourceEventParticipation(2024, ParticipationStatus.Attended);
            b.WithTargetEventParticipation(2024, ParticipationStatus.NotAttending);

            // Year 2025 — source NotAttending loses to target Ticketed.
            b.WithSourceEventParticipation(2025, ParticipationStatus.NotAttending);
            b.WithTargetEventParticipation(2025, ParticipationStatus.Ticketed);

            // Year 2023 — source-only, re-FKs to target.
            b.WithSourceEventParticipation(2023, ParticipationStatus.Attended);
        });
        var requestId = await _factory.SeedMergeRequestAsync(sourceId, targetId);

        var adminId = await SeedAdminUserAsync();
        await AcceptAsync(requestId, adminId);

        await using var assertScope = _factory.Services.CreateAsyncScope();
        var db = assertScope.ServiceProvider.GetRequiredService<HumansDbContext>();

        var targetEvents = await db.EventParticipations
            .AsNoTracking()
            .Where(ep => ep.UserId == targetId)
            .ToListAsync();

        targetEvents.Should().HaveCount(3);
        targetEvents.Single(ep => ep.Year == 2024).Status.Should().Be(ParticipationStatus.Attended);
        targetEvents.Single(ep => ep.Year == 2025).Status.Should().Be(ParticipationStatus.Ticketed);
        targetEvents.Single(ep => ep.Year == 2023).Status.Should().Be(ParticipationStatus.Attended);

        var sourceEvents = await db.EventParticipations
            .AsNoTracking()
            .Where(ep => ep.UserId == sourceId)
            .ToListAsync();
        sourceEvents.Should().BeEmpty();
    }

    // ==================================================================
    // Applications — rule 20
    // ==================================================================

    [HumansFact(Timeout = 30_000)]
    public async Task AcceptAsync_Applications_Move_AllHistorical()
    {
        var (sourceId, targetId) = await _factory.SeedMergeFixtureAsync(b =>
        {
            // Both source and target have applications — every row moves; no dedup.
            b.WithSourceApplication(MembershipTier.Colaborador);
            b.WithSourceApplication(MembershipTier.Asociado);
            b.WithTargetApplication(MembershipTier.Colaborador);
        });
        var requestId = await _factory.SeedMergeRequestAsync(sourceId, targetId);

        var adminId = await SeedAdminUserAsync();
        await AcceptAsync(requestId, adminId);

        await using var assertScope = _factory.Services.CreateAsyncScope();
        var db = assertScope.ServiceProvider.GetRequiredService<HumansDbContext>();

        var targetApps = await db.Applications
            .AsNoTracking()
            .Where(a => a.UserId == targetId)
            .ToListAsync();
        targetApps.Should().HaveCount(3);

        var sourceApps = await db.Applications
            .AsNoTracking()
            .Where(a => a.UserId == sourceId)
            .ToListAsync();
        sourceApps.Should().BeEmpty();
    }

    // ==================================================================
    // FeedbackReports — rule 21 (FeedbackReport part; FeedbackMessage in Pass 2)
    // ==================================================================

    [HumansFact(Timeout = 30_000)]
    public async Task AcceptAsync_FeedbackReportsAndMessages_Move()
    {
        var (sourceId, targetId) = await _factory.SeedMergeFixtureAsync(b =>
        {
            b.WithSourceFeedbackReport("Source bug A");
            b.WithSourceFeedbackReport("Source bug B");
            b.WithTargetFeedbackReport("Target bug C");
        });
        var requestId = await _factory.SeedMergeRequestAsync(sourceId, targetId);

        var adminId = await SeedAdminUserAsync();
        await AcceptAsync(requestId, adminId);

        await using var assertScope = _factory.Services.CreateAsyncScope();
        var db = assertScope.ServiceProvider.GetRequiredService<HumansDbContext>();

        var targetReports = await db.FeedbackReports
            .AsNoTracking()
            .Where(r => r.UserId == targetId)
            .ToListAsync();
        targetReports.Should().HaveCount(3);
        targetReports.Should().ContainSingle(r => r.Description == "Source bug A");
        targetReports.Should().ContainSingle(r => r.Description == "Source bug B");
        targetReports.Should().ContainSingle(r => r.Description == "Target bug C");

        var sourceReports = await db.FeedbackReports
            .AsNoTracking()
            .Where(r => r.UserId == sourceId)
            .ToListAsync();
        sourceReports.Should().BeEmpty();
    }

    // ==================================================================
    // AuditLog — rule 22 (NOT mutated; chain-follow tested in 6.3)
    // ==================================================================

    [HumansFact(Timeout = 30_000)]
    public async Task AcceptAsync_AuditLog_NotMutated_StaysAtSourceId()
    {
        // Per-test description so the post-merge query doesn't pick up rows
        // seeded by other tests in the same shared-DB class fixture.
        var description = $"audit-source-action-{Guid.NewGuid():N}";

        var (sourceId, targetId) = await _factory.SeedMergeFixtureAsync(b =>
        {
            b.WithSourceAuditLogEntry(AuditAction.AccountAnonymized, description);
        });
        var requestId = await _factory.SeedMergeRequestAsync(sourceId, targetId);

        var adminId = await SeedAdminUserAsync();
        await AcceptAsync(requestId, adminId);

        await using var assertScope = _factory.Services.CreateAsyncScope();
        var db = assertScope.ServiceProvider.GetRequiredService<HumansDbContext>();

        // The seeded audit row MUST still be attached to the source user id —
        // fold doesn't mutate audit rows; chain-follow at read time stitches
        // them with the target.
        var seededRows = await db.AuditLogEntries
            .AsNoTracking()
            .Where(a => a.Description == description)
            .ToListAsync();
        seededRows.Should().HaveCount(1);
        seededRows[0].ActorUserId.Should().Be(sourceId);
    }

    // ==================================================================
    // User tombstone + lockout — rules 25, 26
    // ==================================================================

    [HumansFact(Timeout = 30_000)]
    public async Task AcceptAsync_TombstonesSourceWithMergedToUserId()
    {
        var (sourceId, targetId) = await _factory.SeedMergeFixtureAsync();
        var requestId = await _factory.SeedMergeRequestAsync(sourceId, targetId);

        var adminId = await SeedAdminUserAsync();
        await AcceptAsync(requestId, adminId);

        await using var assertScope = _factory.Services.CreateAsyncScope();
        var db = assertScope.ServiceProvider.GetRequiredService<HumansDbContext>();

        var sourceUser = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == sourceId);
        sourceUser.Should().NotBeNull();
        sourceUser!.MergedToUserId.Should().Be(targetId);
        sourceUser.MergedAt.Should().NotBeNull();
        sourceUser.DisplayName.Should().Be("Merged User");
    }

    [HumansFact(Timeout = 30_000)]
    public async Task AcceptAsync_PreventsSourceLogin()
    {
        var (sourceId, targetId) = await _factory.SeedMergeFixtureAsync();
        var requestId = await _factory.SeedMergeRequestAsync(sourceId, targetId);

        var adminId = await SeedAdminUserAsync();
        await AcceptAsync(requestId, adminId);

        await using var assertScope = _factory.Services.CreateAsyncScope();
        var db = assertScope.ServiceProvider.GetRequiredService<HumansDbContext>();

        var sourceUser = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == sourceId);
        sourceUser.Should().NotBeNull();
        sourceUser!.LockoutEnabled.Should().BeTrue();
        sourceUser.LockoutEnd.Should().NotBeNull("source must be locked out forever after merge");
        // AnonymizeForMergeAsync sets LockoutEnd = DateTimeOffset.MaxValue.
        sourceUser.LockoutEnd!.Value.Should().BeCloseTo(DateTimeOffset.MaxValue, TimeSpan.FromDays(1));
    }

    // ==================================================================
    // ContactField — rule 5
    // ==================================================================

    [HumansFact(Timeout = 30_000)]
    public async Task AcceptAsync_ContactFields_Move_DedupOnTypeValue()
    {
        var (sourceId, targetId) = await _factory.SeedMergeFixtureAsync(b =>
        {
            b.WithTargetContactField(ContactFieldType.Phone, "+34 600 100 200");
            b.WithSourceContactField(ContactFieldType.Phone, "+34 600 100 200"); // dup — drop source
            b.WithSourceContactField(ContactFieldType.Telegram, "@source-handle"); // unique to source — moves
            b.WithTargetContactField(ContactFieldType.Telegram, "@target-handle"); // unique to target — stays
        });
        var requestId = await _factory.SeedMergeRequestAsync(sourceId, targetId);

        var adminId = await SeedAdminUserAsync();
        await AcceptAsync(requestId, adminId);

        await using var assertScope = _factory.Services.CreateAsyncScope();
        var db = assertScope.ServiceProvider.GetRequiredService<HumansDbContext>();

        var targetProfileId = await db.Profiles
            .AsNoTracking()
            .Where(p => p.UserId == targetId)
            .Select(p => p.Id)
            .SingleAsync();

        var targetFields = await db.ContactFields
            .AsNoTracking()
            .Where(cf => cf.ProfileId == targetProfileId)
            .ToListAsync();

        // Net union with dedup: target has its two pre-existing rows + the
        // source-only Telegram row re-FK'd. The source's duplicate Phone
        // row was dropped (target's kept).
        targetFields.Should().HaveCount(3);
        targetFields.Should().ContainSingle(cf => cf.FieldType == ContactFieldType.Phone && cf.Value == "+34 600 100 200");
        targetFields.Should().ContainSingle(cf => cf.FieldType == ContactFieldType.Telegram && cf.Value == "@target-handle");
        targetFields.Should().ContainSingle(cf => cf.FieldType == ContactFieldType.Telegram && cf.Value == "@source-handle");

        // Source profile must have no contact fields after the fold.
        var sourceProfileId = await db.Profiles
            .AsNoTracking()
            .Where(p => p.UserId == sourceId)
            .Select(p => p.Id)
            .SingleAsync();

        var sourceFields = await db.ContactFields
            .AsNoTracking()
            .Where(cf => cf.ProfileId == sourceProfileId)
            .ToListAsync();
        sourceFields.Should().BeEmpty();
    }

    // ==================================================================
    // RoleAssignments — rule 11 (Pass 2)
    // ==================================================================

    [HumansFact(Timeout = 30_000)]
    public async Task AcceptAsync_RoleAssignments_ReFKs_DropsSameKey()
    {
        var sharedRole = $"shared-role-{Guid.NewGuid():N}";
        var sourceOnlyRole = $"source-only-role-{Guid.NewGuid():N}";

        var (sourceId, targetId) = await _factory.SeedMergeFixtureAsync(b =>
        {
            // Both have an active assignment for sharedRole — drop source's row.
            b.WithSourceRoleAssignment(sharedRole);
            b.WithTargetRoleAssignment(sharedRole);

            // Source-only — re-FK to target.
            b.WithSourceRoleAssignment(sourceOnlyRole);
        });
        var requestId = await _factory.SeedMergeRequestAsync(sourceId, targetId);

        var adminId = await SeedAdminUserAsync();
        await AcceptAsync(requestId, adminId);

        await using var assertScope = _factory.Services.CreateAsyncScope();
        var db = assertScope.ServiceProvider.GetRequiredService<HumansDbContext>();

        var targetRows = await db.RoleAssignments
            .AsNoTracking()
            .Where(ra => ra.UserId == targetId
                && (ra.RoleName == sharedRole || ra.RoleName == sourceOnlyRole))
            .ToListAsync();

        targetRows.Should().HaveCount(2);
        targetRows.Should().ContainSingle(ra => string.Equals(ra.RoleName, sharedRole, StringComparison.Ordinal));
        targetRows.Should().ContainSingle(ra => string.Equals(ra.RoleName, sourceOnlyRole, StringComparison.Ordinal));

        var sourceRows = await db.RoleAssignments
            .AsNoTracking()
            .Where(ra => ra.UserId == sourceId
                && (ra.RoleName == sharedRole || ra.RoleName == sourceOnlyRole))
            .ToListAsync();
        sourceRows.Should().BeEmpty();
    }

    // ==================================================================
    // TeamMembers — rule 12 (Pass 2)
    // ==================================================================

    [HumansFact(Timeout = 30_000)]
    public async Task AcceptAsync_TeamMembers_AddTargetRemoveSource_NonSystemOnly()
    {
        Guid sharedTeamId = Guid.Empty, sourceOnlyTeamId = Guid.Empty;

        var (sourceId, targetId) = await _factory.SeedMergeFixtureAsync(b =>
        {
            sharedTeamId = b.SeedTeamNow($"Shared-{Guid.NewGuid():N}");
            sourceOnlyTeamId = b.SeedTeamNow($"SourceOnly-{Guid.NewGuid():N}");

            // Both members of sharedTeam — source's slot drops, target stays.
            b.WithSourceTeamMember(sharedTeamId);
            b.WithTargetTeamMember(sharedTeamId);

            // Only source belongs to sourceOnlyTeam — target gets added, source removed.
            b.WithSourceTeamMember(sourceOnlyTeamId);
        });
        var requestId = await _factory.SeedMergeRequestAsync(sourceId, targetId);

        var adminId = await SeedAdminUserAsync();
        await AcceptAsync(requestId, adminId);

        await using var assertScope = _factory.Services.CreateAsyncScope();
        var db = assertScope.ServiceProvider.GetRequiredService<HumansDbContext>();

        // Target should have ACTIVE memberships (LeftAt == null) on both teams.
        var targetActiveTeams = await db.TeamMembers
            .AsNoTracking()
            .Where(tm => tm.UserId == targetId && tm.LeftAt == null
                && (tm.TeamId == sharedTeamId || tm.TeamId == sourceOnlyTeamId))
            .Select(tm => tm.TeamId)
            .ToListAsync();
        targetActiveTeams.Should().Contain(sharedTeamId);
        targetActiveTeams.Should().Contain(sourceOnlyTeamId);

        // Source should have NO active memberships on either team after the fold.
        var sourceActive = await db.TeamMembers
            .AsNoTracking()
            .Where(tm => tm.UserId == sourceId && tm.LeftAt == null
                && (tm.TeamId == sharedTeamId || tm.TeamId == sourceOnlyTeamId))
            .ToListAsync();
        sourceActive.Should().BeEmpty();
    }

    // ==================================================================
    // TeamJoinRequests — rule 13 (Pass 2)
    // ==================================================================

    [HumansFact(Timeout = 30_000)]
    public async Task AcceptAsync_TeamJoinRequests_Move_DropDuplicateActive()
    {
        Guid contestedTeamId = Guid.Empty, sourceOnlyTeamId = Guid.Empty;

        var (sourceId, targetId) = await _factory.SeedMergeFixtureAsync(b =>
        {
            contestedTeamId = b.SeedTeamNow($"Contested-{Guid.NewGuid():N}");
            sourceOnlyTeamId = b.SeedTeamNow($"SourceOnlyTJR-{Guid.NewGuid():N}");

            // Both pending on contestedTeam — drop source.
            b.WithSourceTeamJoinRequest(contestedTeamId, TeamJoinRequestStatus.Pending);
            b.WithTargetTeamJoinRequest(contestedTeamId, TeamJoinRequestStatus.Pending);

            // Source pending on sourceOnly — re-FK to target.
            b.WithSourceTeamJoinRequest(sourceOnlyTeamId, TeamJoinRequestStatus.Pending);
        });
        var requestId = await _factory.SeedMergeRequestAsync(sourceId, targetId);

        var adminId = await SeedAdminUserAsync();
        await AcceptAsync(requestId, adminId);

        await using var assertScope = _factory.Services.CreateAsyncScope();
        var db = assertScope.ServiceProvider.GetRequiredService<HumansDbContext>();

        var targetRequests = await db.TeamJoinRequests
            .AsNoTracking()
            .Where(r => r.UserId == targetId
                && (r.TeamId == contestedTeamId || r.TeamId == sourceOnlyTeamId))
            .ToListAsync();

        // One pending on each team; source's contested-team duplicate dropped.
        targetRequests.Should().HaveCount(2);
        targetRequests.Should().ContainSingle(r => r.TeamId == contestedTeamId);
        targetRequests.Should().ContainSingle(r => r.TeamId == sourceOnlyTeamId);

        var sourceRequests = await db.TeamJoinRequests
            .AsNoTracking()
            .Where(r => r.UserId == sourceId
                && (r.TeamId == contestedTeamId || r.TeamId == sourceOnlyTeamId))
            .ToListAsync();
        sourceRequests.Should().BeEmpty();
    }

    // ==================================================================
    // NotificationRecipients — rule 17 (Pass 2)
    // ==================================================================

    [HumansFact(Timeout = 30_000)]
    public async Task AcceptAsync_NotificationRecipients_Move_DropDuplicate()
    {
        Guid sharedNotificationId = Guid.Empty, sourceOnlyNotificationId = Guid.Empty;

        var (sourceId, targetId) = await _factory.SeedMergeFixtureAsync(b =>
        {
            sharedNotificationId = b.SeedNotificationNow($"shared-{Guid.NewGuid():N}");
            sourceOnlyNotificationId = b.SeedNotificationNow($"source-only-{Guid.NewGuid():N}");

            b.WithSourceNotificationRecipient(sharedNotificationId);
            b.WithTargetNotificationRecipient(sharedNotificationId);
            b.WithSourceNotificationRecipient(sourceOnlyNotificationId);
        });
        var requestId = await _factory.SeedMergeRequestAsync(sourceId, targetId);

        var adminId = await SeedAdminUserAsync();
        await AcceptAsync(requestId, adminId);

        await using var assertScope = _factory.Services.CreateAsyncScope();
        var db = assertScope.ServiceProvider.GetRequiredService<HumansDbContext>();

        var targetRecipients = await db.NotificationRecipients
            .AsNoTracking()
            .Where(nr => nr.UserId == targetId
                && (nr.NotificationId == sharedNotificationId || nr.NotificationId == sourceOnlyNotificationId))
            .ToListAsync();

        targetRecipients.Should().HaveCount(2, "duplicate on shared notification dropped, source-only re-FK'd");
        targetRecipients.Should().ContainSingle(nr => nr.NotificationId == sharedNotificationId);
        targetRecipients.Should().ContainSingle(nr => nr.NotificationId == sourceOnlyNotificationId);

        var sourceRecipients = await db.NotificationRecipients
            .AsNoTracking()
            .Where(nr => nr.UserId == sourceId
                && (nr.NotificationId == sharedNotificationId || nr.NotificationId == sourceOnlyNotificationId))
            .ToListAsync();
        sourceRecipients.Should().BeEmpty();
    }

    // ==================================================================
    // CampaignGrants — rule 18 (Pass 2)
    // ==================================================================

    [HumansFact(Timeout = 30_000)]
    public async Task AcceptAsync_CampaignGrants_Move_DedupPerCampaign()
    {
        Guid contestedCampaignId = Guid.Empty, sourceOnlyCampaignId = Guid.Empty;
        Guid creatorId = Guid.Empty;

        var (sourceId, targetId) = await _factory.SeedMergeFixtureAsync();

        // Need a real user to satisfy Campaign.CreatedByUserId FK; use the
        // source as creator (fold doesn't touch Campaigns).
        creatorId = sourceId;

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HumansDbContext>();
            var builder = new MergeFixtureBuilder(scope, sourceId, targetId);
            contestedCampaignId = builder.SeedCampaignNow($"Contested-{Guid.NewGuid():N}", creatorId);
            sourceOnlyCampaignId = builder.SeedCampaignNow($"SourceOnly-{Guid.NewGuid():N}", creatorId);

            builder
                .WithSourceCampaignGrant(contestedCampaignId)
                .WithTargetCampaignGrant(contestedCampaignId)
                .WithSourceCampaignGrant(sourceOnlyCampaignId);

            await builder.SaveAllAsync();
        }
        var requestId = await _factory.SeedMergeRequestAsync(sourceId, targetId);

        var adminId = await SeedAdminUserAsync();
        await AcceptAsync(requestId, adminId);

        await using var assertScope = _factory.Services.CreateAsyncScope();
        var db2 = assertScope.ServiceProvider.GetRequiredService<HumansDbContext>();

        var targetGrants = await db2.CampaignGrants
            .AsNoTracking()
            .Where(g => g.UserId == targetId
                && (g.CampaignId == contestedCampaignId || g.CampaignId == sourceOnlyCampaignId))
            .ToListAsync();
        targetGrants.Should().HaveCount(2);
        targetGrants.Should().ContainSingle(g => g.CampaignId == contestedCampaignId);
        targetGrants.Should().ContainSingle(g => g.CampaignId == sourceOnlyCampaignId);

        var sourceGrants = await db2.CampaignGrants
            .AsNoTracking()
            .Where(g => g.UserId == sourceId
                && (g.CampaignId == contestedCampaignId || g.CampaignId == sourceOnlyCampaignId))
            .ToListAsync();
        sourceGrants.Should().BeEmpty();
    }

    // ==================================================================
    // FeedbackMessages — rule 21 part 2 (Pass 2)
    // ==================================================================

    [HumansFact(Timeout = 30_000)]
    public async Task AcceptAsync_FeedbackMessages_Move()
    {
        Guid reportId = Guid.Empty;
        var sourceContent = $"source-msg-{Guid.NewGuid():N}";
        var targetContent = $"target-msg-{Guid.NewGuid():N}";

        var (sourceId, targetId) = await _factory.SeedMergeFixtureAsync();

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var builder = new MergeFixtureBuilder(scope, sourceId, targetId);
            // FeedbackMessages.SenderUserId references either user; the
            // FeedbackReport itself can belong to either side too. Use the
            // target's report so it's untouched by the merge (the message
            // SenderUserId moves source -> target).
            reportId = builder.SeedFeedbackReportNow(targetId, $"report-{Guid.NewGuid():N}");

            builder
                .WithSourceFeedbackMessage(reportId, sourceContent)
                .WithTargetFeedbackMessage(reportId, targetContent);

            await builder.SaveAllAsync();
        }
        var requestId = await _factory.SeedMergeRequestAsync(sourceId, targetId);

        var adminId = await SeedAdminUserAsync();
        await AcceptAsync(requestId, adminId);

        await using var assertScope = _factory.Services.CreateAsyncScope();
        var db = assertScope.ServiceProvider.GetRequiredService<HumansDbContext>();

        // Both messages should now be attributed to target.
        var targetMessages = await db.FeedbackMessages
            .AsNoTracking()
            .Where(m => m.SenderUserId == targetId
                && (m.Content == sourceContent || m.Content == targetContent))
            .ToListAsync();
        targetMessages.Should().HaveCount(2);

        var sourceMessages = await db.FeedbackMessages
            .AsNoTracking()
            .Where(m => m.SenderUserId == sourceId)
            .ToListAsync();
        sourceMessages.Should().BeEmpty();
    }

    // ==================================================================
    // BudgetAuditLog — rule 24 (Pass 2)
    // Append-only — fold MUST NOT mutate; chain-follow stitches at read time.
    // ==================================================================

    [HumansFact(Timeout = 30_000)]
    public async Task AcceptAsync_BudgetAuditLog_NotMutated_StaysAtSourceId()
    {
        var description = $"budget-source-action-{Guid.NewGuid():N}";

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

        await using var assertScope = _factory.Services.CreateAsyncScope();
        var db = assertScope.ServiceProvider.GetRequiredService<HumansDbContext>();

        // Audit row must still point at the source user — fold doesn't
        // mutate append-only logs; chain-follow at read time stitches them
        // with the target.
        var rows = await db.BudgetAuditLogs
            .AsNoTracking()
            .Where(l => l.Description == description)
            .ToListAsync();
        rows.Should().HaveCount(1);
        rows[0].ActorUserId.Should().Be(sourceId);
    }

    // ==================================================================
    // Helpers
    // ==================================================================

    private async Task AcceptAsync(Guid requestId, Guid adminUserId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var mergeService = scope.ServiceProvider.GetRequiredService<IAccountMergeService>();
        await mergeService.AcceptAsync(requestId, adminUserId);
    }
}
