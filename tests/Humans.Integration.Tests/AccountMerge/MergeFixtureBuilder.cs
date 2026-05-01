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
    // Deferred — phase 6.2 test author adds these as cases come online.
    // Each requires more cross-section setup than the minimum-viable
    // fixture warrants this PR.
    // ==================================================================

    // TODO(phase 6.x): seed Ticket — needs a TicketOrder + TicketAttendee
    //   pair and lives in the Tickets section; the order is keyed by user.

    // TODO(phase 6.x): seed RoleAssignment — Auth section. Requires a known
    //   role name + valid-from instant; safe to add when the per-rule test
    //   for role-assignment chain follow lands.

    // TODO(phase 6.x): seed TeamMember — Teams section. Needs a Team row;
    //   either pre-seed in the test or extend with WithSourceTeamId helper.

    // TODO(phase 6.x): seed TeamJoinRequest — Teams section. Same prereq
    //   as TeamMember (existing Team row).

    // TODO(phase 6.x): seed VolunteerEventProfile — needs an EventSettings
    //   row; phase 6.2 test for that rule pre-seeds it.

    // TODO(phase 6.x): seed VolunteerTagPreference — needs a ShiftTag row.

    // TODO(phase 6.x): seed GeneralAvailability — needs an EventSettings
    //   row.

    // TODO(phase 6.x): seed CampaignGrant — needs a Campaign row.

    // TODO(phase 6.x): seed CampLead — needs a Camp + CampSeason chain.

    // TODO(phase 6.x): seed CampRoleAssignment — needs Camp + CampSeason
    //   + CampRoleDefinition.

    // TODO(phase 6.x): seed FeedbackMessage — straightforward; just needs
    //   a FeedbackReport.Id from a prior WithSource/TargetFeedbackReport.

    // TODO(phase 6.x): seed ConsentRecord — append-only; needs a published
    //   DocumentVersion. Insert via db.ConsentRecords.Add (DB triggers
    //   block update/delete but allow insert).

    // TODO(phase 6.x): seed BudgetAuditLog — append-only Budget section
    //   row; chain-follow read mirrors the audit-log pattern.

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
