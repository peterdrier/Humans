using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

// Mirror the DbContext alias: the CLR type is Humans.Domain.Entities.Application
// but a sibling Humans.Application namespace shadows it inside this project.
using MemberApplication = Humans.Domain.Entities.Application;

namespace Humans.Integration.Tests.AccountMerge;

/// <summary>
/// Fluent seed-builder for the AccountMergeService fold-into-target
/// integration tests. Each <c>WithSourceX</c>/<c>WithTargetX</c> stages an
/// entity in memory; <see cref="SaveAllAsync"/> flushes them in a single
/// <c>SaveChanges</c>.
/// <para>
/// Phase 6.1 implements the minimum surface phase 6.2 needs to write the
/// per-rule fold tests. Methods that require a richer per-section setup
/// (Camp seasons, Tickets, Team rows, Notifications, Budget, etc.) are
/// stubbed with <c>TODO(phase 6.x)</c> markers so the test author can grow
/// the fixture as cases come online.
/// </para>
/// </summary>
public sealed class MergeFixtureBuilder
{
    private readonly IServiceScope _scope;
    private readonly HumansDbContext _db;
    private readonly Instant _now;
    private readonly List<Action<HumansDbContext>> _pending = [];

    public Guid SourceUserId { get; }
    public Guid TargetUserId { get; }

    internal MergeFixtureBuilder(IServiceScope scope, Guid sourceUserId, Guid targetUserId)
    {
        _scope = scope;
        _db = scope.ServiceProvider.GetRequiredService<HumansDbContext>();
        _now = SystemClock.Instance.GetCurrentInstant();
        SourceUserId = sourceUserId;
        TargetUserId = targetUserId;
    }

    // ------------------------------------------------------------------
    // UserEmail (Users section)
    // ------------------------------------------------------------------

    public MergeFixtureBuilder WithSourceEmail(
        string email,
        bool verified = false,
        bool isPrimary = false,
        bool isGoogle = false)
        => AddEmail(SourceUserId, email, verified, isPrimary, isGoogle);

    public MergeFixtureBuilder WithTargetEmail(
        string email,
        bool verified = false,
        bool isPrimary = false,
        bool isGoogle = false)
        => AddEmail(TargetUserId, email, verified, isPrimary, isGoogle);

    private MergeFixtureBuilder AddEmail(
        Guid userId, string email, bool verified, bool isPrimary, bool isGoogle)
    {
        _pending.Add(db => db.UserEmails.Add(new UserEmail
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Email = email,
            IsVerified = verified,
            IsPrimary = isPrimary,
            IsGoogle = isGoogle,
            Provider = isGoogle ? "Google" : null,
            ProviderKey = isGoogle ? $"sub-{Guid.NewGuid():N}" : null,
            CreatedAt = _now,
            UpdatedAt = _now,
        }));
        return this;
    }

    // ------------------------------------------------------------------
    // ContactField (Profiles section)
    // ------------------------------------------------------------------

    public MergeFixtureBuilder WithSourceContactField(ContactFieldType type, string value)
        => AddContactField(SourceUserId, type, value);

    public MergeFixtureBuilder WithTargetContactField(ContactFieldType type, string value)
        => AddContactField(TargetUserId, type, value);

    private MergeFixtureBuilder AddContactField(Guid userId, ContactFieldType type, string value)
    {
        _pending.Add(db =>
        {
            var profile = db.Profiles.AsTracking().Single(p => p.UserId == userId);
            db.ContactFields.Add(new ContactField
            {
                Id = Guid.NewGuid(),
                ProfileId = profile.Id,
                FieldType = type,
                Value = value,
                Visibility = ContactFieldVisibility.AllActiveProfiles,
                CreatedAt = _now,
                UpdatedAt = _now,
            });
        });
        return this;
    }

    // ------------------------------------------------------------------
    // VolunteerHistory (Profiles section)
    // ------------------------------------------------------------------

    public MergeFixtureBuilder WithSourceVolunteerHistory(int year, string eventName)
        => AddVolunteerHistory(SourceUserId, year, eventName);

    public MergeFixtureBuilder WithTargetVolunteerHistory(int year, string eventName)
        => AddVolunteerHistory(TargetUserId, year, eventName);

    private MergeFixtureBuilder AddVolunteerHistory(Guid userId, int year, string eventName)
    {
        _pending.Add(db =>
        {
            var profile = db.Profiles.AsTracking().Single(p => p.UserId == userId);
            db.VolunteerHistoryEntries.Add(new VolunteerHistoryEntry
            {
                Id = Guid.NewGuid(),
                ProfileId = profile.Id,
                Date = new LocalDate(year, 1, 1),
                EventName = eventName,
                CreatedAt = _now,
                UpdatedAt = _now,
            });
        });
        return this;
    }

    // ------------------------------------------------------------------
    // Language / ProfileLanguage (Profiles section)
    // ------------------------------------------------------------------

    public MergeFixtureBuilder WithSourceLanguage(string code, LanguageProficiency proficiency)
        => AddLanguage(SourceUserId, code, proficiency);

    public MergeFixtureBuilder WithTargetLanguage(string code, LanguageProficiency proficiency)
        => AddLanguage(TargetUserId, code, proficiency);

    private MergeFixtureBuilder AddLanguage(Guid userId, string code, LanguageProficiency proficiency)
    {
        _pending.Add(db =>
        {
            var profile = db.Profiles.AsTracking().Single(p => p.UserId == userId);
            db.ProfileLanguages.Add(new ProfileLanguage
            {
                Id = Guid.NewGuid(),
                ProfileId = profile.Id,
                LanguageCode = code,
                Proficiency = proficiency,
            });
        });
        return this;
    }

    // ------------------------------------------------------------------
    // CommunicationPreference (Users section)
    // ------------------------------------------------------------------

    public MergeFixtureBuilder WithSourceCommPref(MessageCategory category, bool optedOut, Instant? updatedAt = null)
        => AddCommPref(SourceUserId, category, optedOut, updatedAt);

    public MergeFixtureBuilder WithTargetCommPref(MessageCategory category, bool optedOut, Instant? updatedAt = null)
        => AddCommPref(TargetUserId, category, optedOut, updatedAt);

    private MergeFixtureBuilder AddCommPref(Guid userId, MessageCategory category, bool optedOut, Instant? updatedAt)
    {
        _pending.Add(db => db.CommunicationPreferences.Add(new CommunicationPreference
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Category = category,
            OptedOut = optedOut,
            UpdatedAt = updatedAt ?? _now,
            UpdateSource = "TestFixture",
        }));
        return this;
    }

    // ------------------------------------------------------------------
    // AspNetUserLogin (Auth section — Identity table)
    // ------------------------------------------------------------------

    public MergeFixtureBuilder WithSourceLogin(string loginProvider, string providerKey)
        => AddLogin(SourceUserId, loginProvider, providerKey);

    public MergeFixtureBuilder WithTargetLogin(string loginProvider, string providerKey)
        => AddLogin(TargetUserId, loginProvider, providerKey);

    private MergeFixtureBuilder AddLogin(Guid userId, string loginProvider, string providerKey)
    {
        _pending.Add(db => db.Set<Microsoft.AspNetCore.Identity.IdentityUserLogin<Guid>>().Add(
            new Microsoft.AspNetCore.Identity.IdentityUserLogin<Guid>
            {
                UserId = userId,
                LoginProvider = loginProvider,
                ProviderKey = providerKey,
                ProviderDisplayName = loginProvider,
            }));
        return this;
    }

    // ------------------------------------------------------------------
    // EventParticipation (Camps / Participation)
    // ------------------------------------------------------------------

    public MergeFixtureBuilder WithSourceEventParticipation(int year, ParticipationStatus status)
        => AddEventParticipation(SourceUserId, year, status);

    public MergeFixtureBuilder WithTargetEventParticipation(int year, ParticipationStatus status)
        => AddEventParticipation(TargetUserId, year, status);

    private MergeFixtureBuilder AddEventParticipation(Guid userId, int year, ParticipationStatus status)
    {
        _pending.Add(db => db.EventParticipations.Add(new EventParticipation
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Year = year,
            Status = status,
            Source = ParticipationSource.UserDeclared,
            DeclaredAt = status == ParticipationStatus.NotAttending ? _now : null,
        }));
        return this;
    }

    // ------------------------------------------------------------------
    // ShiftSignup (Shifts section)
    //
    // Note: requires an existing Shift row. Tests that exercise the shift
    // fold rule are responsible for seeding the Shift+Rota themselves and
    // passing its Id; this helper just attaches the signup.
    // ------------------------------------------------------------------

    public MergeFixtureBuilder WithSourceShiftSignup(Guid shiftId)
        => AddShiftSignup(SourceUserId, shiftId);

    public MergeFixtureBuilder WithTargetShiftSignup(Guid shiftId)
        => AddShiftSignup(TargetUserId, shiftId);

    private MergeFixtureBuilder AddShiftSignup(Guid userId, Guid shiftId)
    {
        _pending.Add(db => db.ShiftSignups.Add(new ShiftSignup
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ShiftId = shiftId,
            Status = SignupStatus.Confirmed,
            CreatedAt = _now,
            UpdatedAt = _now,
        }));
        return this;
    }

    // ------------------------------------------------------------------
    // NotificationRecipient (Notifications section)
    //
    // Note: requires a Notification row to already exist; phase 6.2 tests
    // exercising the notification-fold rule pre-seed it themselves.
    // ------------------------------------------------------------------

    public MergeFixtureBuilder WithSourceNotificationRecipient(Guid notificationId)
        => AddNotificationRecipient(SourceUserId, notificationId);

    public MergeFixtureBuilder WithTargetNotificationRecipient(Guid notificationId)
        => AddNotificationRecipient(TargetUserId, notificationId);

    private MergeFixtureBuilder AddNotificationRecipient(Guid userId, Guid notificationId)
    {
        _pending.Add(db => db.NotificationRecipients.Add(new NotificationRecipient
        {
            NotificationId = notificationId,
            UserId = userId,
        }));
        return this;
    }

    // ------------------------------------------------------------------
    // Application (Governance / tier applications — entity is mapped as
    // MemberApplication in the DbContext but the CLR type is Application).
    // ------------------------------------------------------------------

    public MergeFixtureBuilder WithSourceApplication(MembershipTier tier = MembershipTier.Colaborador)
        => AddApplication(SourceUserId, tier);

    public MergeFixtureBuilder WithTargetApplication(MembershipTier tier = MembershipTier.Colaborador)
        => AddApplication(TargetUserId, tier);

    private MergeFixtureBuilder AddApplication(Guid userId, MembershipTier tier)
    {
        _pending.Add(db => db.Applications.Add(new MemberApplication
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            MembershipTier = tier,
            Motivation = "Test motivation",
            SubmittedAt = _now,
            UpdatedAt = _now,
        }));
        return this;
    }

    // ------------------------------------------------------------------
    // FeedbackReport (Feedback section)
    // ------------------------------------------------------------------

    public MergeFixtureBuilder WithSourceFeedbackReport(string title)
        => AddFeedbackReport(SourceUserId, title);

    public MergeFixtureBuilder WithTargetFeedbackReport(string title)
        => AddFeedbackReport(TargetUserId, title);

    private MergeFixtureBuilder AddFeedbackReport(Guid userId, string title)
    {
        _pending.Add(db => db.FeedbackReports.Add(new FeedbackReport
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Category = FeedbackCategory.Bug,
            Description = title,
            PageUrl = "/test",
            Status = FeedbackStatus.Open,
            CreatedAt = _now,
            UpdatedAt = _now,
        }));
        return this;
    }

    // ------------------------------------------------------------------
    // AuditLogEntry (AuditLog section)
    //
    // Audit log is append-only and per-user via ActorUserId / RelatedEntityId.
    // For the fold tests we attach to ActorUserId which is what the
    // chain-follow read uses.
    // ------------------------------------------------------------------

    public MergeFixtureBuilder WithSourceAuditLogEntry(AuditAction action, string description)
    {
        _pending.Add(db => db.AuditLogEntries.Add(new AuditLogEntry
        {
            Id = Guid.NewGuid(),
            Action = action,
            EntityType = "User",
            EntityId = SourceUserId,
            Description = description,
            OccurredAt = _now,
            ActorUserId = SourceUserId,
        }));
        return this;
    }

    // ==================================================================
    // RoleAssignment (Auth section)
    // ==================================================================

    public MergeFixtureBuilder WithSourceRoleAssignment(
        string roleName, Instant? validFrom = null, Instant? validTo = null)
        => AddRoleAssignment(SourceUserId, roleName, validFrom, validTo);

    public MergeFixtureBuilder WithTargetRoleAssignment(
        string roleName, Instant? validFrom = null, Instant? validTo = null)
        => AddRoleAssignment(TargetUserId, roleName, validFrom, validTo);

    private MergeFixtureBuilder AddRoleAssignment(
        Guid userId, string roleName, Instant? validFrom, Instant? validTo)
    {
        _pending.Add(db => db.RoleAssignments.Add(new RoleAssignment
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            RoleName = roleName,
            ValidFrom = validFrom ?? _now.Minus(Duration.FromDays(30)),
            ValidTo = validTo,
            CreatedAt = _now,
            CreatedByUserId = userId, // self-assign for fixture purposes
        }));
        return this;
    }

    // ==================================================================
    // Team / TeamMember / TeamJoinRequest (Teams section)
    //
    // The fixture creates non-system teams ad hoc, returning the Team.Id
    // so tests can assert against it. System teams are excluded from the
    // fold, so we always build IsSystemTeam = false here.
    // ==================================================================

    public Guid SeedTeamNow(string name)
    {
        var teamId = Guid.NewGuid();
        // IsSystemTeam is computed (SystemTeamType == None). Leaving
        // SystemTeamType at default None gives IsSystemTeam = false, which
        // is what the merge fold expects for the TeamMember rule.
        var team = new Team
        {
            Id = teamId,
            Name = name,
            Slug = $"team-{teamId:N}".Substring(0, 12),
            IsActive = true,
            CreatedAt = _now,
            UpdatedAt = _now,
        };
        _db.Teams.Add(team);
        _db.SaveChanges();
        return teamId;
    }

    public MergeFixtureBuilder WithSourceTeamMember(Guid teamId)
        => AddTeamMember(SourceUserId, teamId);

    public MergeFixtureBuilder WithTargetTeamMember(Guid teamId)
        => AddTeamMember(TargetUserId, teamId);

    private MergeFixtureBuilder AddTeamMember(Guid userId, Guid teamId)
    {
        _pending.Add(db => db.TeamMembers.Add(new TeamMember
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            UserId = userId,
            JoinedAt = _now,
        }));
        return this;
    }

    public MergeFixtureBuilder WithSourceTeamJoinRequest(
        Guid teamId, TeamJoinRequestStatus status = TeamJoinRequestStatus.Pending)
        => AddTeamJoinRequest(SourceUserId, teamId, status);

    public MergeFixtureBuilder WithTargetTeamJoinRequest(
        Guid teamId, TeamJoinRequestStatus status = TeamJoinRequestStatus.Pending)
        => AddTeamJoinRequest(TargetUserId, teamId, status);

    private MergeFixtureBuilder AddTeamJoinRequest(
        Guid userId, Guid teamId, TeamJoinRequestStatus status)
    {
        _pending.Add(db => db.TeamJoinRequests.Add(new TeamJoinRequest
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            UserId = userId,
            Status = status,
            RequestedAt = _now,
        }));
        return this;
    }

    // ==================================================================
    // Notification / NotificationRecipient (Notifications section)
    // ==================================================================

    public Guid SeedNotificationNow(string title)
    {
        var notificationId = Guid.NewGuid();
        var notification = new Notification
        {
            Id = notificationId,
            Title = title,
            Priority = NotificationPriority.Normal,
            Source = NotificationSource.SyncError,
            Class = NotificationClass.Informational,
            CreatedAt = _now,
        };
        _db.Notifications.Add(notification);
        _db.SaveChanges();
        return notificationId;
    }

    // ==================================================================
    // Campaign / CampaignGrant (Campaigns section)
    // ==================================================================

    public Guid SeedCampaignNow(string title, Guid creatorUserId)
    {
        var campaignId = Guid.NewGuid();
        var campaign = new Campaign
        {
            Id = campaignId,
            Title = title,
            EmailSubject = $"{title} subject",
            EmailBodyTemplate = "test body",
            Status = CampaignStatus.Draft,
            CreatedAt = _now,
            CreatedByUserId = creatorUserId,
        };
        _db.Campaigns.Add(campaign);

        // CampaignGrant requires a CampaignCode FK; seed one alongside the
        // campaign so tests can attach grants without extra plumbing.
        var code = new CampaignCode
        {
            Id = Guid.NewGuid(),
            CampaignId = campaignId,
            Code = $"code-{campaignId:N}",
            ImportOrder = 0,
            ImportedAt = _now,
        };
        _db.CampaignCodes.Add(code);
        _db.SaveChanges();
        return campaignId;
    }

    public MergeFixtureBuilder WithSourceCampaignGrant(Guid campaignId)
        => AddCampaignGrant(SourceUserId, campaignId);

    public MergeFixtureBuilder WithTargetCampaignGrant(Guid campaignId)
        => AddCampaignGrant(TargetUserId, campaignId);

    private MergeFixtureBuilder AddCampaignGrant(Guid userId, Guid campaignId)
    {
        _pending.Add(db =>
        {
            // One CampaignCode per grant (1:1 nav on the entity). Seed a
            // fresh code per grant so two grants on the same campaign
            // don't collide on the unique CampaignCodeId.
            var codeId = Guid.NewGuid();
            db.CampaignCodes.Add(new CampaignCode
            {
                Id = codeId,
                CampaignId = campaignId,
                Code = $"code-{codeId:N}",
                ImportOrder = 0,
                ImportedAt = _now,
            });
            db.CampaignGrants.Add(new CampaignGrant
            {
                Id = Guid.NewGuid(),
                CampaignId = campaignId,
                CampaignCodeId = codeId,
                UserId = userId,
                AssignedAt = _now,
            });
        });
        return this;
    }

    // ==================================================================
    // FeedbackMessage (Feedback section)
    // ==================================================================

    public Guid SeedFeedbackReportNow(Guid userId, string description)
    {
        var reportId = Guid.NewGuid();
        var report = new FeedbackReport
        {
            Id = reportId,
            UserId = userId,
            Category = FeedbackCategory.Bug,
            Description = description,
            PageUrl = "/test",
            Status = FeedbackStatus.Open,
            CreatedAt = _now,
            UpdatedAt = _now,
        };
        _db.FeedbackReports.Add(report);
        _db.SaveChanges();
        return reportId;
    }

    public MergeFixtureBuilder WithSourceFeedbackMessage(Guid reportId, string content)
        => AddFeedbackMessage(SourceUserId, reportId, content);

    public MergeFixtureBuilder WithTargetFeedbackMessage(Guid reportId, string content)
        => AddFeedbackMessage(TargetUserId, reportId, content);

    private MergeFixtureBuilder AddFeedbackMessage(Guid userId, Guid reportId, string content)
    {
        _pending.Add(db => db.FeedbackMessages.Add(new FeedbackMessage
        {
            Id = Guid.NewGuid(),
            FeedbackReportId = reportId,
            SenderUserId = userId,
            Content = content,
            CreatedAt = _now,
        }));
        return this;
    }

    // ==================================================================
    // BudgetAuditLog (Budget section)
    //
    // Append-only with DB triggers blocking UPDATE/DELETE; INSERT is the
    // only legal mutation. Chain-follow read mirrors the audit-log pattern.
    // ==================================================================

    public Guid SeedBudgetYearNow(string yearLabel)
    {
        var budgetYearId = Guid.NewGuid();
        var year = new BudgetYear
        {
            Id = budgetYearId,
            Year = yearLabel,
            Name = $"Budget {yearLabel}",
            CreatedAt = _now,
            UpdatedAt = _now,
        };
        _db.BudgetYears.Add(year);
        _db.SaveChanges();
        return budgetYearId;
    }

    public MergeFixtureBuilder WithSourceBudgetAuditLog(
        Guid budgetYearId, string description)
    {
        _pending.Add(db => db.BudgetAuditLogs.Add(new BudgetAuditLog
        {
            Id = Guid.NewGuid(),
            BudgetYearId = budgetYearId,
            EntityType = "BudgetYear",
            EntityId = budgetYearId,
            Description = description,
            ActorUserId = SourceUserId,
            OccurredAt = _now,
        }));
        return this;
    }

    // ==================================================================
    // ConsentRecord (Legal & Consent section)
    //
    // Append-only with DB triggers blocking UPDATE/DELETE; INSERT is the
    // only legal mutation. Chain-follow read mirrors the audit-log pattern.
    // Seeding requires a Team -> LegalDocument -> DocumentVersion chain;
    // <see cref="SeedDocumentVersionNow"/> builds that minimal chain in one
    // shot and returns the version id callers pass into
    // <see cref="WithSourceConsentRecord"/>.
    // ==================================================================

    public Guid SeedDocumentVersionNow(string documentName = "TestDoc")
    {
        var teamId = Guid.NewGuid();
        var docId = Guid.NewGuid();
        var versionId = Guid.NewGuid();

        _db.Teams.Add(new Team
        {
            Id = teamId,
            Name = $"ConsentTeam-{teamId:N}".Substring(0, 16),
            Slug = $"team-{teamId:N}".Substring(0, 12),
            IsActive = true,
            CreatedAt = _now,
            UpdatedAt = _now,
        });
        _db.LegalDocuments.Add(new LegalDocument
        {
            Id = docId,
            Name = documentName,
            TeamId = teamId,
            IsActive = true,
            IsRequired = true,
            CurrentCommitSha = "abc",
            CreatedAt = _now,
            LastSyncedAt = _now,
        });
        _db.DocumentVersions.Add(new DocumentVersion
        {
            Id = versionId,
            LegalDocumentId = docId,
            VersionNumber = "v1",
            CommitSha = "abc",
            Content = new Dictionary<string, string>(StringComparer.Ordinal) { ["es"] = "text" },
            EffectiveFrom = _now,
            CreatedAt = _now,
        });
        _db.SaveChanges();
        return versionId;
    }

    public MergeFixtureBuilder WithSourceConsentRecord(Guid documentVersionId, bool explicitConsent = true)
    {
        _pending.Add(db => db.ConsentRecords.Add(new ConsentRecord
        {
            Id = Guid.NewGuid(),
            UserId = SourceUserId,
            DocumentVersionId = documentVersionId,
            ConsentedAt = _now,
            IpAddress = "127.0.0.1",
            UserAgent = "TestFixture",
            ContentHash = "hash",
            ExplicitConsent = explicitConsent,
        }));
        return this;
    }

    // ==================================================================
    // Deferred — heavier chains; phase 6.2 author can pull these in as
    // needed.
    // ==================================================================

    // TODO(phase 6.x): seed Ticket — needs a TicketOrder + TicketAttendee
    //   pair and lives in the Tickets section; the order is keyed by user.

    // TODO(phase 6.x): seed VolunteerEventProfile — needs an EventSettings
    //   row; phase 6.2 test for that rule pre-seeds it.

    // TODO(phase 6.x): seed VolunteerTagPreference — needs a ShiftTag row.

    // TODO(phase 6.x): seed GeneralAvailability — needs an EventSettings
    //   row.

    // TODO(phase 6.x): seed ShiftSignup helper for phase 6.2 — the existing
    //   builder takes a Shift.Id but seeding the Shift requires a Rota +
    //   EventSettings + Team. Defer until a rota fixture is needed.

    // TODO(phase 6.x): seed CampLead — needs a Camp + CampSeason chain.

    // TODO(phase 6.x): seed CampRoleAssignment — needs Camp + CampSeason
    //   + CampRoleDefinition.

    // ------------------------------------------------------------------

    /// <summary>
    /// Flushes everything staged via the With* methods in a single
    /// <c>SaveChanges</c>. Called automatically by
    /// <see cref="MergeFixtureExtensions.SeedMergeFixtureAsync"/>.
    /// </summary>
    public async Task SaveAllAsync()
    {
        foreach (var apply in _pending)
        {
            apply(_db);
        }
        _pending.Clear();
        await _db.SaveChangesAsync();
    }
}
