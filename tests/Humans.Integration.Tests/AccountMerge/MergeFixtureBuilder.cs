using Humans.Governance.Contracts;
using Humans.Shifts.Data;
using Humans.Shifts.Domain;
using Humans.Auth.Data;
using Humans.Auth.Domain;
using Humans.AuditLog.Data;
using Humans.AuditLog.Domain;
using Humans.Teams.Data;
using Humans.Teams.Domain;
using Humans.Consent.Domain;
using Humans.Consent.Data;
using Humans.Budget.Data;
using Humans.Budget.Domain;
using Humans.Campaigns.Data;
using Humans.Campaigns.Domain;
using Humans.Feedback.Data;
using Humans.Governance.Data;
using Humans.Feedback.Domain;
using Humans.Notifications.Data;
using Humans.Notifications.Domain;
using Humans.Notifications.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

using MemberApplication = Humans.Governance.Domain.Application;
using Humans.Users.Contracts;
using Humans.Users.Data;
using Humans.Users.Domain;
using Humans.Teams.Contracts;
using Humans.Shifts.Contracts;
using Humans.AuditLog.Contracts;
using Humans.Feedback.Contracts;

namespace Humans.Integration.Tests.AccountMerge;

/// <summary>
/// Each <c>WithSourceX</c>/<c>WithTargetX</c> stages an entity in memory; <see cref="SaveAllAsync"/> flushes them in
/// one <c>SaveChanges</c>.
/// </summary>
public sealed class MergeFixtureBuilder
{
    private readonly UsersDbContext _db;

    // Separate contexts over the same database, so staged rows still flush together.
    private readonly AuthDbContext _authDb;
    private readonly NotificationsDbContext _notificationsDb;
    private readonly GovernanceDbContext _governanceDb;
    private readonly CampaignsDbContext _campaignsDb;
    private readonly FeedbackDbContext _feedbackDb;
    private readonly BudgetDbContext _budgetDb;
    private readonly LegalDbContext _legalDb;
    private readonly AuditLogDbContext _auditLogDb;
    private readonly ShiftsDbContext _shiftsDb;
    private readonly TeamsDbContext _teamsDb;

    private readonly Instant _now;
    private readonly List<Action<UsersDbContext>> _pending = [];
    private readonly List<Action<AuthDbContext>> _pendingAuth = [];
    private readonly List<Action<NotificationsDbContext>> _pendingNotifications = [];
    private readonly List<Action<GovernanceDbContext>> _pendingGovernance = [];
    private readonly List<Action<CampaignsDbContext>> _pendingCampaigns = [];
    private readonly List<Action<FeedbackDbContext>> _pendingFeedback = [];
    private readonly List<Action<BudgetDbContext>> _pendingBudget = [];
    private readonly List<Action<LegalDbContext>> _pendingLegal = [];
    private readonly List<Action<AuditLogDbContext>> _pendingAuditLog = [];
    private readonly List<Action<ShiftsDbContext>> _pendingShifts = [];
    private readonly List<Action<TeamsDbContext>> _pendingTeams = [];

    public Guid SourceUserId { get; }
    public Guid TargetUserId { get; }

    internal MergeFixtureBuilder(IServiceScope scope, Guid sourceUserId, Guid targetUserId)
    {
        _db = scope.ServiceProvider.GetRequiredService<UsersDbContext>();
        _authDb = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        _notificationsDb = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
        _governanceDb = scope.ServiceProvider.GetRequiredService<GovernanceDbContext>();
        _campaignsDb = scope.ServiceProvider.GetRequiredService<CampaignsDbContext>();
        _feedbackDb = scope.ServiceProvider.GetRequiredService<FeedbackDbContext>();
        _budgetDb = scope.ServiceProvider.GetRequiredService<BudgetDbContext>();
        _legalDb = scope.ServiceProvider.GetRequiredService<LegalDbContext>();
        _auditLogDb = scope.ServiceProvider.GetRequiredService<AuditLogDbContext>();
        _shiftsDb = scope.ServiceProvider.GetRequiredService<ShiftsDbContext>();
        _teamsDb = scope.ServiceProvider.GetRequiredService<TeamsDbContext>();
        _now = SystemClock.Instance.GetCurrentInstant();
        SourceUserId = sourceUserId;
        TargetUserId = targetUserId;
    }

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

    // Users' frozen legacy table; Email reads it as a fallback until its own row shadows it.
    public MergeFixtureBuilder WithSourceCommPref(LegacyMessageCategory category, bool optedOut, Instant? updatedAt = null)
        => AddCommPref(SourceUserId, category, optedOut, updatedAt);

    public MergeFixtureBuilder WithTargetCommPref(LegacyMessageCategory category, bool optedOut, Instant? updatedAt = null)
        => AddCommPref(TargetUserId, category, optedOut, updatedAt);

    private MergeFixtureBuilder AddCommPref(Guid userId, LegacyMessageCategory category, bool optedOut, Instant? updatedAt)
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

    // The frozen legacy Users table; Tickets' fold materializes it into tickets_event_participations.
    public MergeFixtureBuilder WithSourceEventParticipation(int year, LegacyParticipationStatus status)
        => AddEventParticipation(SourceUserId, year, status);

    public MergeFixtureBuilder WithTargetEventParticipation(int year, LegacyParticipationStatus status)
        => AddEventParticipation(TargetUserId, year, status);

    private MergeFixtureBuilder AddEventParticipation(Guid userId, int year, LegacyParticipationStatus status)
    {
        _pending.Add(db => db.EventParticipations.Add(new EventParticipation
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Year = year,
            Status = status,
            Source = LegacyParticipationSource.UserDeclared,
            DeclaredAt = status == LegacyParticipationStatus.NotAttending ? _now : null,
        }));
        return this;
    }

    // Needs an existing Shift; the caller seeds the Shift and Rota.
    public MergeFixtureBuilder WithSourceShiftSignup(Guid shiftId)
        => AddShiftSignup(SourceUserId, shiftId);

    public MergeFixtureBuilder WithTargetShiftSignup(Guid shiftId)
        => AddShiftSignup(TargetUserId, shiftId);

    private MergeFixtureBuilder AddShiftSignup(Guid userId, Guid shiftId)
    {
        _pendingShifts.Add(db => db.ShiftSignups.Add(new ShiftSignup
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

    // Needs an existing Notification row.
    public MergeFixtureBuilder WithSourceNotificationRecipient(Guid notificationId)
        => AddNotificationRecipient(SourceUserId, notificationId);

    public MergeFixtureBuilder WithTargetNotificationRecipient(Guid notificationId)
        => AddNotificationRecipient(TargetUserId, notificationId);

    private MergeFixtureBuilder AddNotificationRecipient(Guid userId, Guid notificationId)
    {
        _pendingNotifications.Add(db => db.NotificationRecipients.Add(new NotificationRecipient
        {
            NotificationId = notificationId,
            UserId = userId,
        }));
        return this;
    }

    public MergeFixtureBuilder WithSourceApplication(MembershipTier tier = MembershipTier.Colaborador)
        => AddApplication(SourceUserId, tier);

    public MergeFixtureBuilder WithTargetApplication(MembershipTier tier = MembershipTier.Colaborador)
        => AddApplication(TargetUserId, tier);

    private MergeFixtureBuilder AddApplication(Guid userId, MembershipTier tier)
    {
        _pendingGovernance.Add(db => db.Applications.Add(new MemberApplication
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

    public MergeFixtureBuilder WithSourceFeedbackReport(string title)
        => AddFeedbackReport(SourceUserId, title);

    public MergeFixtureBuilder WithTargetFeedbackReport(string title)
        => AddFeedbackReport(TargetUserId, title);

    private MergeFixtureBuilder AddFeedbackReport(Guid userId, string title)
    {
        _pendingFeedback.Add(db => db.FeedbackReports.Add(new FeedbackReport
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

    // Attached via ActorUserId, which the chain-follow read uses.
    public MergeFixtureBuilder WithSourceAuditLogEntry(AuditAction action, string description)
    {
        _pendingAuditLog.Add(db => db.AuditLogEntries.Add(new AuditLogEntry
        {
            Id = Guid.NewGuid(),
            Action = action.ToString(),
            EntityType = "User",
            EntityId = SourceUserId,
            Description = description,
            OccurredAt = _now,
            ActorUserId = SourceUserId,
        }));
        return this;
    }

    public MergeFixtureBuilder WithSourceRoleAssignment(
        string roleName, Instant? validFrom = null, Instant? validTo = null)
        => AddRoleAssignment(SourceUserId, roleName, validFrom, validTo);

    public MergeFixtureBuilder WithTargetRoleAssignment(
        string roleName, Instant? validFrom = null, Instant? validTo = null)
        => AddRoleAssignment(TargetUserId, roleName, validFrom, validTo);

    private MergeFixtureBuilder AddRoleAssignment(
        Guid userId, string roleName, Instant? validFrom, Instant? validTo)
    {
        _pendingAuth.Add(db => db.RoleAssignments.Add(new RoleAssignment
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

    // Non-system teams only: system teams are excluded from the fold.
    public Guid SeedTeamNow(string name)
    {
        var teamId = Guid.NewGuid();
        // SystemTeamType None makes IsSystemTeam false, as the fold expects.
        var team = new Team
        {
            Id = teamId,
            Name = name,
            Slug = $"team-{teamId:N}".Substring(0, 12),
            IsActive = true,
            CreatedAt = _now,
            UpdatedAt = _now,
        };
        _teamsDb.Teams.Add(team);
        _teamsDb.SaveChanges();
        return teamId;
    }

    public MergeFixtureBuilder WithSourceTeamMember(Guid teamId)
        => AddTeamMember(SourceUserId, teamId);

    public MergeFixtureBuilder WithTargetTeamMember(Guid teamId)
        => AddTeamMember(TargetUserId, teamId);

    private MergeFixtureBuilder AddTeamMember(Guid userId, Guid teamId)
    {
        _pendingTeams.Add(db => db.TeamMembers.Add(new TeamMember
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
        _pendingTeams.Add(db => db.TeamJoinRequests.Add(new TeamJoinRequest
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            UserId = userId,
            Status = status,
            RequestedAt = _now,
        }));
        return this;
    }

    public Guid SeedNotificationNow(string title)
    {
        var notificationId = Guid.NewGuid();
        var notification = new Notification
        {
            Id = notificationId,
            Title = title,
            Priority = NotificationPriority.Normal,
            Source = "SyncError",
            Class = NotificationClass.Informational,
            CreatedAt = _now,
        };
        _notificationsDb.Notifications.Add(notification);
        _notificationsDb.SaveChanges();
        return notificationId;
    }

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
        _campaignsDb.Campaigns.Add(campaign);

        // CampaignGrant needs a CampaignCode FK.
        var code = new CampaignCode
        {
            Id = Guid.NewGuid(),
            CampaignId = campaignId,
            Code = $"code-{campaignId:N}",
            ImportOrder = 0,
            ImportedAt = _now,
        };
        _campaignsDb.CampaignCodes.Add(code);
        _campaignsDb.SaveChanges();
        return campaignId;
    }

    public MergeFixtureBuilder WithSourceCampaignGrant(Guid campaignId)
        => AddCampaignGrant(SourceUserId, campaignId);

    public MergeFixtureBuilder WithTargetCampaignGrant(Guid campaignId)
        => AddCampaignGrant(TargetUserId, campaignId);

    private MergeFixtureBuilder AddCampaignGrant(Guid userId, Guid campaignId)
    {
        _pendingCampaigns.Add(db =>
        {
            // One code per grant: CampaignCodeId is unique.
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
        _feedbackDb.FeedbackReports.Add(report);
        _feedbackDb.SaveChanges();
        return reportId;
    }

    public MergeFixtureBuilder WithSourceFeedbackMessage(Guid reportId, string content)
        => AddFeedbackMessage(SourceUserId, reportId, content);

    public MergeFixtureBuilder WithTargetFeedbackMessage(Guid reportId, string content)
        => AddFeedbackMessage(TargetUserId, reportId, content);

    private MergeFixtureBuilder AddFeedbackMessage(Guid userId, Guid reportId, string content)
    {
        _pendingFeedback.Add(db => db.FeedbackMessages.Add(new FeedbackMessage
        {
            Id = Guid.NewGuid(),
            FeedbackReportId = reportId,
            SenderUserId = userId,
            Content = content,
            CreatedAt = _now,
        }));
        return this;
    }

    // Append-only (DB triggers block UPDATE/DELETE): insert only.
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
        _budgetDb.BudgetYears.Add(year);
        _budgetDb.SaveChanges();
        return budgetYearId;
    }

    public MergeFixtureBuilder WithSourceBudgetAuditLog(
        Guid budgetYearId, string description)
    {
        _pendingBudget.Add(db => db.BudgetAuditLogs.Add(new BudgetAuditLog
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

    // Append-only (DB triggers block UPDATE/DELETE). SeedDocumentVersionNow builds the chain a record needs.
    public Guid SeedDocumentVersionNow(string documentName = "TestDoc")
    {
        var teamId = Guid.NewGuid();
        var docId = Guid.NewGuid();
        var versionId = Guid.NewGuid();

        _teamsDb.Teams.Add(new Team
        {
            Id = teamId,
            Name = $"ConsentTeam-{teamId:N}".Substring(0, 16),
            Slug = $"team-{teamId:N}".Substring(0, 12),
            IsActive = true,
            CreatedAt = _now,
            UpdatedAt = _now,
        });
        _legalDb.LegalDocuments.Add(new LegalDocument
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
        _legalDb.DocumentVersions.Add(new DocumentVersion
        {
            Id = versionId,
            LegalDocumentId = docId,
            VersionNumber = "v1",
            CommitSha = "abc",
            Content = new Dictionary<string, string>(StringComparer.Ordinal) { ["es"] = "text" },
            EffectiveFrom = _now,
            CreatedAt = _now,
        });
        _teamsDb.SaveChanges();
        _legalDb.SaveChanges();
        return versionId;
    }

    public MergeFixtureBuilder WithSourceConsentRecord(Guid documentVersionId, bool explicitConsent = true)
    {
        _pendingLegal.Add(db => db.ConsentRecords.Add(new ConsentRecord
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

    /// <summary>Called by <see cref="MergeFixtureExtensions.SeedMergeFixtureAsync"/>.</summary>
    public async Task SaveAllAsync()
    {
        foreach (var apply in _pending)
        {
            apply(_db);
        }
        _pending.Clear();
        await _db.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        foreach (var apply in _pendingAuth)
        {
            apply(_authDb);
        }
        _pendingAuth.Clear();
        await _authDb.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        foreach (var apply in _pendingNotifications)
        {
            apply(_notificationsDb);
        }
        _pendingNotifications.Clear();
        await _notificationsDb.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        foreach (var apply in _pendingGovernance)
        {
            apply(_governanceDb);
        }
        _pendingGovernance.Clear();
        await _governanceDb.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        foreach (var apply in _pendingCampaigns)
        {
            apply(_campaignsDb);
        }
        _pendingCampaigns.Clear();
        await _campaignsDb.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        foreach (var apply in _pendingFeedback)
        {
            apply(_feedbackDb);
        }
        _pendingFeedback.Clear();
        await _feedbackDb.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        foreach (var apply in _pendingBudget)
        {
            apply(_budgetDb);
        }
        _pendingBudget.Clear();
        await _budgetDb.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        foreach (var apply in _pendingLegal)
        {
            apply(_legalDb);
        }
        _pendingLegal.Clear();
        await _legalDb.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        foreach (var apply in _pendingAuditLog)
        {
            apply(_auditLogDb);
        }
        _pendingAuditLog.Clear();
        await _auditLogDb.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        foreach (var apply in _pendingShifts)
        {
            apply(_shiftsDb);
        }
        _pendingShifts.Clear();
        await _shiftsDb.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        foreach (var apply in _pendingTeams)
        {
            apply(_teamsDb);
        }
        _pendingTeams.Clear();
        await _teamsDb.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
    }
}
