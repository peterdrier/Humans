using Humans.Governance.Contracts;
using NodaTime;
using Stateless;

namespace Humans.Governance.Domain;

/// <summary>Colaborador or Asociado only — never Volunteer.</summary>
internal sealed class Application
{
    private StateMachine<ApplicationStatus, ApplicationTrigger>? _stateMachine;

    public Guid Id { get; init; }

    public Guid UserId { get; init; }

    public MembershipTier MembershipTier { get; set; }

    public ApplicationStatus Status { get; private set; } = ApplicationStatus.Submitted;

    public string Motivation { get; set; } = string.Empty;

    public string? AdditionalInfo { get; set; }

    public string? SignificantContribution { get; set; }

    public string? RoleUnderstanding { get; set; }

    public string? Language { get; set; }

    public Instant SubmittedAt { get; init; }

    public Instant UpdatedAt { get; set; }

    public Instant? ReviewStartedAt { get; private set; }

    public Instant? ResolvedAt { get; private set; }

    public Guid? ReviewedByUserId { get; private set; }

    public string? ReviewNotes { get; private set; }

    /// <summary>Dec 31 of the cycle's odd year; set on approval.</summary>
    public LocalDate? TermExpiresAt { get; set; }

    public LocalDate? BoardMeetingDate { get; set; }

    /// <summary>
    /// Board's collective decision note. Required for rejection, optional for approval.
    /// This is the only record of the Board's reasoning — individual votes are deleted (GDPR).
    /// </summary>
    public string? DecisionNote { get; set; }

    public Instant? RenewalReminderSentAt { get; set; }

    public ICollection<ApplicationStateHistory> StateHistory { get; } = new List<ApplicationStateHistory>();

    public ICollection<BoardVote> BoardVotes { get; } = new List<BoardVote>();

    public void ValidateTier()
    {
        if (MembershipTier == MembershipTier.Volunteer)
        {
            throw new InvalidOperationException(
                "Applications are for Colaborador or Asociado tiers only. Volunteer access does not require an application.");
        }
    }

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
