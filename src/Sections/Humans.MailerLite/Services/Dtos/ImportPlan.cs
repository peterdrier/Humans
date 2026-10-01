namespace Humans.MailerLite.Services.Dtos;

internal sealed record ImportPlan(
    IReadOnlyList<SubscriberDecision> Decisions,
    int TotalPulled)
{
    public ImportPlanCounts Counts { get; } = new(
        CreateNewHuman: Decisions.Count(d => d.Outcome == SubscriberOutcome.CreateNewHuman),
        ReplaceUnverifiedEmail: Decisions.Count(d => d.Outcome == SubscriberOutcome.ReplaceUnverifiedEmail),
        VerifiedPrefsAlreadyMatch: Decisions.Count(d => d.Outcome == SubscriberOutcome.VerifiedPrefsAlreadyMatch),
        VerifiedFlipToOptIn: Decisions.Count(d => d.Outcome == SubscriberOutcome.VerifiedFlipToOptIn),
        VerifiedFlipToOptOut: Decisions.Count(d => d.Outcome == SubscriberOutcome.VerifiedFlipToOptOut),
        VerifiedKeepHumansPref: Decisions.Count(d => d.Outcome == SubscriberOutcome.VerifiedKeepHumansPref),
        ResetMarketingFlag: Decisions.Count(d => d.Outcome == SubscriberOutcome.ResetMarketingFlag),
        AmbiguousMultipleVerified: Decisions.Count(d => d.Outcome == SubscriberOutcome.AmbiguousMultipleVerified),
        UnconfirmedSkipped: Decisions.Count(d => d.Outcome == SubscriberOutcome.UnconfirmedSkipped));
}

internal sealed record ImportPlanCounts(
    int CreateNewHuman,
    int ReplaceUnverifiedEmail,
    int VerifiedPrefsAlreadyMatch,
    int VerifiedFlipToOptIn,
    int VerifiedFlipToOptOut,
    int VerifiedKeepHumansPref,
    int ResetMarketingFlag,
    int AmbiguousMultipleVerified,
    int UnconfirmedSkipped)
{
    /// <summary>
    /// True when any outcome's count moved more than 10% from <paramref name="preview"/> (or
    /// became non-zero from zero) — the plan an admin confirmed is no longer the plan that
    /// would be applied.
    /// </summary>
    public bool DriftedMoreThanTenPercentFrom(ImportPlanCounts preview)
    {
        static bool D(int prev, int now)
        {
            if (prev == 0) return now > 0;
            return Math.Abs(now - prev) / (double)prev > 0.10;
        }
        return D(preview.CreateNewHuman, CreateNewHuman)
            || D(preview.ReplaceUnverifiedEmail, ReplaceUnverifiedEmail)
            || D(preview.VerifiedPrefsAlreadyMatch, VerifiedPrefsAlreadyMatch)
            || D(preview.VerifiedFlipToOptIn, VerifiedFlipToOptIn)
            || D(preview.VerifiedFlipToOptOut, VerifiedFlipToOptOut)
            || D(preview.VerifiedKeepHumansPref, VerifiedKeepHumansPref)
            || D(preview.ResetMarketingFlag, ResetMarketingFlag)
            || D(preview.AmbiguousMultipleVerified, AmbiguousMultipleVerified)
            || D(preview.UnconfirmedSkipped, UnconfirmedSkipped);
    }
}
