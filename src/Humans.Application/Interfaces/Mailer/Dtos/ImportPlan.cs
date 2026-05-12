namespace Humans.Application.Interfaces.Mailer.Dtos;

public sealed record ImportPlan(
    IReadOnlyList<SubscriberDecision> Decisions,
    int TotalPulled)
{
    public ImportPlanCounts Counts { get; } = new(
        Decisions.Count(d => d.Outcome == SubscriberOutcome.CreateContact),
        Decisions.Count(d => d.Outcome == SubscriberOutcome.AttachVerified),
        Decisions.Count(d => d.Outcome == SubscriberOutcome.AttachVerifiedConfirmOnly),
        Decisions.Count(d => d.Outcome == SubscriberOutcome.AttachVerifiedConflictKept),
        Decisions.Count(d => d.Outcome == SubscriberOutcome.DeleteUnverifiedThenCreate),
        Decisions.Count(d => d.Outcome == SubscriberOutcome.AmbiguousMultipleVerified),
        Decisions.Count(d => d.Outcome == SubscriberOutcome.UnconfirmedSkipped));
}

public sealed record ImportPlanCounts(
    int WillCreateContact,
    int WillAttachWithFlip,
    int WillAttachConfirmOnly,
    int WillKeepHumansState,
    int WillDeleteUnverifiedAndCreate,
    int SkippedAmbiguous,
    int SkippedUnconfirmed);
