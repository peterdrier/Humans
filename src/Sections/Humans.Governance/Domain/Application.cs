using Humans.Users.Contracts;
using Humans.Governance.Contracts;
using NodaTime;
using Stateless;

namespace Humans.Governance.Domain;

/// <summary>
/// Tier application entity with state machine workflow.
/// Used for Colaborador and Asociado applications (never Volunteer).
/// During initial signup, created inline alongside the profile.
/// After onboarding, created via the dedicated Application route.
/// </summary>
internal sealed class Application
{
    private StateMachine<ApplicationStatus, ApplicationTrigger>? _stateMachine;

    public Guid Id { get; init; }

    /// <summary>
    /// Foreign key to the applicant user. Use <c>IUserService.GetUserInfoAsync</c>
    /// or <c>IUserServiceRead.GetUserInfosAsync</c> to hydrate user info — cross-domain
    /// navigation properties are forbidden on this entity (design-rules §6).
    /// </summary>
    public Guid UserId { get; init; }

    /// <summary>
    /// The membership tier being applied for (Colaborador or Asociado — never Volunteer).
    /// </summary>
    public MembershipTier MembershipTier { get; set; }

    /// <summary>
    /// Current status of the application.
    /// </summary>
    public ApplicationStatus Status { get; private set; } = ApplicationStatus.Submitted;

    public string Motivation { get; set; } = string.Empty;

    public string? AdditionalInfo { get; set; }

    public string? SignificantContribution { get; set; }

    public string? RoleUnderstanding { get; set; }

    /// <summary>
    /// The UI language the applicant was using when they submitted the application (ISO 639-1 code, e.g. "es", "en").
    /// </summary>
    public string? Language { get; set; }

    public Instant SubmittedAt { get; init; }

    public Instant UpdatedAt { get; set; }

    /// <summary>
    /// When the review started.
    /// </summary>
    public Instant? ReviewStartedAt { get; private set; }

    public Instant? ResolvedAt { get; private set; }

    /// <summary>
    /// ID of the reviewer who processed the application. Use
    /// <c>IUserService</c> to hydrate reviewer info — cross-domain
    /// navigation properties are forbidden on this entity.
    /// </summary>
    public Guid? ReviewedByUserId { get; private set; }

    public string? ReviewNotes { get; private set; }

    /// <summary>
    /// When the membership term expires. Set on approval: Dec 31 of the appropriate odd year.
    /// Null until approved. Only applies to Colaborador/Asociado.
    /// </summary>
    public LocalDate? TermExpiresAt { get; set; }

    public LocalDate? BoardMeetingDate { get; set; }

    /// <summary>
    /// Board's collective decision note. Required for rejection, optional for approval.
    /// This is the only record of the Board's reasoning — individual votes are deleted (GDPR).
    /// </summary>
    public string? DecisionNote { get; set; }

    /// <summary>
    /// When the renewal reminder email was last sent for this application's term.
    /// Used to prevent sending duplicate reminders.
    /// </summary>
    public Instant? RenewalReminderSentAt { get; set; }

    public ICollection<ApplicationStateHistory> StateHistory { get; } = new List<ApplicationStateHistory>();

    public ICollection<BoardVote> BoardVotes { get; } = new List<BoardVote>();

    /// <summary>
    /// Validates that the membership tier is appropriate for an application
    /// (Colaborador or Asociado only, never Volunteer).
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when tier is Volunteer.</exception>
    public void ValidateTier()
    {
        if (MembershipTier == MembershipTier.Volunteer)
        {
            throw new InvalidOperationException(
                "Applications are for Colaborador or Asociado tiers only. Volunteer access does not require an application.");
        }
    }

    /// <summary>
    /// Gets the state machine for this application.
    /// </summary>
    public StateMachine<ApplicationStatus, ApplicationTrigger> StateMachine =>
        _stateMachine ??= CreateStateMachine();

    private StateMachine<ApplicationStatus, ApplicationTrigger> CreateStateMachine()
    {
        var machine = new StateMachine<ApplicationStatus, ApplicationTrigger>(
            () => Status,
            s => Status = s);

        machine.Configure(ApplicationStatus.Submitted)
            .Permit(ApplicationTrigger.Approve, ApplicationStatus.Approved)
            .Permit(ApplicationTrigger.Reject, ApplicationStatus.Rejected)
            .PermitReentry(ApplicationTrigger.RequestMoreInfo)
            .Permit(ApplicationTrigger.Withdraw, ApplicationStatus.Withdrawn);

        machine.Configure(ApplicationStatus.Approved);

        machine.Configure(ApplicationStatus.Rejected);

        machine.Configure(ApplicationStatus.Withdrawn);

        return machine;
    }

    public void Approve(Guid reviewerUserId, string? notes, IClock clock)
    {
        StateMachine.Fire(ApplicationTrigger.Approve);
        ReviewedByUserId = reviewerUserId;
        ReviewNotes = notes;
        var now = clock.GetCurrentInstant();
        UpdatedAt = now;
        ResolvedAt = now;
        AddStateHistory(ApplicationStatus.Approved, reviewerUserId, clock, notes);
    }

    public void Reject(Guid reviewerUserId, string reason, IClock clock)
    {
        StateMachine.Fire(ApplicationTrigger.Reject);
        ReviewedByUserId = reviewerUserId;
        ReviewNotes = reason;
        var now = clock.GetCurrentInstant();
        UpdatedAt = now;
        ResolvedAt = now;
        AddStateHistory(ApplicationStatus.Rejected, reviewerUserId, clock, reason);
    }

    public void Withdraw(IClock clock)
    {
        StateMachine.Fire(ApplicationTrigger.Withdraw);
        var now = clock.GetCurrentInstant();
        UpdatedAt = now;
        ResolvedAt = now;
        AddStateHistory(ApplicationStatus.Withdrawn, UserId, clock);
    }

    /// <summary>
    /// Requests more information from the applicant.
    /// </summary>
    /// <param name="reviewerUserId">The ID of the reviewer.</param>
    /// <param name="notes">Notes about what information is needed.</param>
    /// <param name="clock">The clock to use for timestamps.</param>
    public void RequestMoreInfo(Guid reviewerUserId, string notes, IClock clock)
    {
        StateMachine.Fire(ApplicationTrigger.RequestMoreInfo);
        ReviewNotes = notes;
        UpdatedAt = clock.GetCurrentInstant();
        AddStateHistory(ApplicationStatus.Submitted, reviewerUserId, clock, notes);
    }

    private void AddStateHistory(ApplicationStatus newStatus, Guid actorUserId, IClock clock, string? notes = null)
    {
        StateHistory.Add(new ApplicationStateHistory
        {
            ApplicationId = Id,
            Status = newStatus,
            ChangedByUserId = actorUserId,
            ChangedAt = clock.GetCurrentInstant(),
            Notes = notes
        });
    }
}
