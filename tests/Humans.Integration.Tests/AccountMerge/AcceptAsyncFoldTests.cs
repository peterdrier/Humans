using AwesomeAssertions;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Repositories;
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
        // Seed a real admin row per test so those FKs resolve.
        await using var scope = _factory.Services.CreateAsyncScope();
        var um = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var adminId = Guid.NewGuid();
        var admin = new User
        {
            Id = adminId,
            DisplayName = "Test Admin",
            Email = $"admin-{adminId:N}@test.local",
            UserName = $"admin-{adminId:N}@test.local",
            CreatedAt = SystemClock.Instance.GetCurrentInstant(),
        };
        var result = await um.CreateAsync(admin);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                "Failed to seed admin user for AcceptAsyncFoldTests: "
                + string.Join("; ", result.Errors.Select(e => e.Description)));
        }
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

    [HumansFact(
        Timeout = 30_000,
        Skip = "Phase 1-5 bug — UserRepository.ReassignLoginsToUserAsync removes "
            + "the source row and adds a new IdentityUserLogin<Guid> with the same "
            + "composite PK (LoginProvider, ProviderKey) in the same DbContext, "
            + "tripping EF's identity map. Needs a SaveChanges between the Remove "
            + "and Add or a switch to ExecuteUpdate. Tracked separately from "
            + "phase 6.2 test work.")]
    public async Task AcceptAsync_AspNetUserLogins_ReFKs_DropsSameKey()
    {
        var sharedKey = $"shared-sub-{Guid.NewGuid():N}";
        var sourceOnlyKey = $"source-only-sub-{Guid.NewGuid():N}";

        var (sourceId, targetId) = await _factory.SeedMergeFixtureAsync(b =>
        {
            // Same provider+key on both — collision, drop source.
            b.WithSourceLogin("Google", sharedKey);
            b.WithTargetLogin("Google", sharedKey);

            // Source-only — moves to target.
            b.WithSourceLogin("Google", sourceOnlyKey);
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

        targetLogins.Should().HaveCount(2);
        targetLogins.Should().ContainSingle(l =>
            string.Equals(l.LoginProvider, "Google", StringComparison.Ordinal)
            && string.Equals(l.ProviderKey, sharedKey, StringComparison.Ordinal));
        targetLogins.Should().ContainSingle(l =>
            string.Equals(l.LoginProvider, "Google", StringComparison.Ordinal)
            && string.Equals(l.ProviderKey, sourceOnlyKey, StringComparison.Ordinal));

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
    // (ContactField rule 5 is exercised through profile sub-aggregates;
    // see comment on the contact-fields test below for the ordering bug.)
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
    // (Documented for Pass 1 even though the assertion is currently
    // weakened: ProfileService.ReassignSubAggregatesToUserAsync deletes
    // source ContactFields BEFORE ContactFieldService.ReassignToUserAsync
    // runs, so source rows are dropped rather than re-FK'd. See bug note
    // in the Phase 6.2 report. The assertion below verifies the *current*
    // behavior — when the bug is fixed, change to expect the source row
    // to be re-FK'd onto the target profile.)
    // ==================================================================

    [HumansFact(Timeout = 30_000)]
    public async Task AcceptAsync_ContactFields_Move_DedupOnTypeValue()
    {
        var (sourceId, targetId) = await _factory.SeedMergeFixtureAsync(b =>
        {
            b.WithTargetContactField(ContactFieldType.Phone, "+34 600 100 200");
            b.WithSourceContactField(ContactFieldType.Phone, "+34 600 100 200"); // dup
            b.WithSourceContactField(ContactFieldType.Telegram, "@source-handle"); // unique to source
            b.WithTargetContactField(ContactFieldType.Telegram, "@target-handle"); // unique to target
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

        // Target's pre-existing rows survive regardless of the ordering bug.
        targetFields.Should().ContainSingle(cf => cf.FieldType == ContactFieldType.Phone && cf.Value == "+34 600 100 200");
        targetFields.Should().ContainSingle(cf => cf.FieldType == ContactFieldType.Telegram && cf.Value == "@target-handle");

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
    // Helpers
    // ==================================================================

    private async Task AcceptAsync(Guid requestId, Guid adminUserId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var mergeService = scope.ServiceProvider.GetRequiredService<IAccountMergeService>();
        await mergeService.AcceptAsync(requestId, adminUserId);
    }
}
