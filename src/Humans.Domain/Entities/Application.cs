using NodaTime;
using Humans.Domain.Enums;
using Stateless;

namespace Humans.Domain.Entities;

/// <summary>
/// Tier application entity with state machine workflow.
/// Used for Colaborador and Asociado applications (never Volunteer).
/// During initial signup, created inline alongside the profile.
/// After onboarding, created via the dedicated Application route.
/// </summary>
public class Application
{
    private StateMachine<ApplicationStatus, ApplicationTrigger>? _stateMachine;

    /// <summary>
    /// Unique identifier for the application.
    /// </summary>
    public Guid Id { get; init; }

    /// <summary>
    /// Foreign key to the applicant user.
    /// </summary>
    public Guid UserId { get; init; }

    /// <summary>
    /// Navigation property to the applicant.
    /// </summary>
    public User User { get; set; } = null!;

    /// <summary>
    /// The membership tier being applied for (Colaborador or Asociado — never Volunteer).
    /// </summary>
    public MembershipTier MembershipTier { get; set; }

    /// <summary>
    /// Current status of the application.
    /// </summary>
    public ApplicationStatus Status { get; private set; } = ApplicationStatus.Submitted;

    /// <summary>
    /// Application motivation statement.
    /// </summary>
    public string Motivation { get; set; } = string.Empty;

    /// <summary>
    /// Additional information provided by the applicant.
    /// </summary>
    public string? AdditionalInfo { get; set; }

    /// <summary>
    /// Asociado-only: the applicant's most significant contribution to Nowhere (or another Burn).
    /// </summary>
    public string? SignificantContribution { get; set; }

    /// <summary>
    /// Asociado-only: the applicant's understanding of the asociado role and why they want it.
    /// </summary>
    public string? RoleUnderstanding { get; set; }

    /// <summary>
    /// The UI language the applicant was using when they submitted the application (ISO 639-1 code, e.g. "es", "en").
    /// </summary>
    public string? Language { get; set; }

    /// <summary>
    /// When the application was submitted.
    /// </summary>
    public Instant SubmittedAt { get; init; }

    /// <summary>
    /// When the application was last updated.
    /// </summary>
    public Instant UpdatedAt { get; set; }

    /// <summary>
    /// When the review started.
    /// </summary>
    public Instant? ReviewStartedAt { get; private set; }

    /// <summary>
    /// When the application was resolved (approved/rejected/withdrawn).
    /// </summary>
    public Instant? ResolvedAt { get; private set; }

    /// <summary>
    /// ID of the reviewer who processed the application.
    /// </summary>
    public Guid? ReviewedByUserId { get; private set; }

    /// <summary>
    /// Navigation property to the reviewer.
    /// </summary>
    public User? ReviewedByUser { get; set; }

    /// <summary>
    /// Reason for rejection or notes from reviewer.
    /// </summary>
    public string? ReviewNotes { get; private set; }

    /// <summary>
    /// When the membership term expires. Set on approval: Dec 31 of the appropriate odd year.
    /// Null until approved. Only applies to Colaborador/Asociado.
    /// </summary>
    public LocalDate? TermExpiresAt { get; set; }

    /// <summary>
    /// Date of the Board meeting where the decision was made.
    /// Required when finalizing a Board vote.
    /// </summary>
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

    /// <summary>
    /// Navigation property to state history.
    /// </summary>
    public ICollection<ApplicationStateHistory> StateHistory { get; } = new List<ApplicationStateHistory>();

    /// <summary>
    /// Navigation property to Board votes (transient — deleted on finalization).
    /// </summary>
    public ICollection<BoardVote> BoardVotes { get; } = new List<BoardVote>();

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

        machine.Configure(ApplicationStatus.Approved)
            .OnEntry(() => ResolvedAt = SystemClock.Instance.GetCurrentInstant());

        machine.Configure(ApplicationStatus.Rejected)
            .OnEntry(() => ResolvedAt = SystemClock.Instance.GetCurrentInstant());

        machine.Configure(ApplicationStatus.Withdrawn)
            .OnEntry(() => ResolvedAt = SystemClock.Instance.GetCurrentInstant());

        return machine;
    }

    /// <summary>
    /// Approves this application.
    /// </summary>
    /// <param name="reviewerUserId">The ID of the reviewer.</param>
    /// <param name="notes">Optional notes.</param>
    /// <param name="clock">The clock to use for timestamps.</param>
    public void Approve(Guid reviewerUserId, string? notes, IClock clock)
    {
        StateMachine.Fire(ApplicationTrigger.Approve);
        ReviewedByUserId = reviewerUserId;
        ReviewNotes = notes;
        UpdatedAt = clock.GetCurrentInstant();
        AddStateHistory(ApplicationStatus.Approved, reviewerUserId, clock, notes);
    }

    /// <summary>
    /// Rejects this application.
    /// </summary>
    /// <param name="reviewerUserId">The ID of the reviewer.</param>
    /// <param name="reason">The reason for rejection.</param>
    /// <param name="clock">The clock to use for timestamps.</param>
    public void Reject(Guid reviewerUserId, string reason, IClock clock)
    {
        StateMachine.Fire(ApplicationTrigger.Reject);
        ReviewedByUserId = reviewerUserId;
        ReviewNotes = reason;
        UpdatedAt = clock.GetCurrentInstant();
        AddStateHistory(ApplicationStatus.Rejected, reviewerUserId, clock, reason);
    }

    /// <summary>
    /// Withdraws this application.
    /// </summary>
    /// <param name="clock">The clock to use for timestamps.</param>
    public void Withdraw(IClock clock)
    {
        StateMachine.Fire(ApplicationTrigger.Withdraw);
        UpdatedAt = clock.GetCurrentInstant();
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
            Id = Guid.NewGuid(),
            ApplicationId = Id,
            Status = newStatus,
            ChangedByUserId = actorUserId,
            ChangedAt = clock.GetCurrentInstant(),
            Notes = notes
        });
    }
}
