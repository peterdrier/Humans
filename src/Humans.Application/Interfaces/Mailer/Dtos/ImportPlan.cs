namespace Humans.Application.Interfaces.Mailer.Dtos;

public sealed record ImportPlan(
    IReadOnlyList<SubscriberDecision> Decisions,
    int TotalPulled)
{
    public ImportPlanCounts Counts { get; } = new(
        CreateNewHuman:            Decisions.Count(d => d.Outcome == SubscriberOutcome.CreateNewHuman),
        ReplaceUnverifiedEmail:    Decisions.Count(d => d.Outcome == SubscriberOutcome.ReplaceUnverifiedEmail),
        VerifiedPrefsAlreadyMatch: Decisions.Count(d => d.Outcome == SubscriberOutcome.VerifiedPrefsAlreadyMatch),
        VerifiedFlipToOptIn:       Decisions.Count(d => d.Outcome == SubscriberOutcome.VerifiedFlipToOptIn),
        VerifiedFlipToOptOut:      Decisions.Count(d => d.Outcome == SubscriberOutcome.VerifiedFlipToOptOut),
        VerifiedKeepHumansPref:    Decisions.Count(d => d.Outcome == SubscriberOutcome.VerifiedKeepHumansPref),
        AmbiguousMultipleVerified: Decisions.Count(d => d.Outcome == SubscriberOutcome.AmbiguousMultipleVerified),
        UnconfirmedSkipped:        Decisions.Count(d => d.Outcome == SubscriberOutcome.UnconfirmedSkipped));
}

public sealed record ImportPlanCounts(
    int CreateNewHuman,
    int ReplaceUnverifiedEmail,
    int VerifiedPrefsAlreadyMatch,
    int VerifiedFlipToOptIn,
    int VerifiedFlipToOptOut,
    int VerifiedKeepHumansPref,
    int AmbiguousMultipleVerified,
    int UnconfirmedSkipped);
