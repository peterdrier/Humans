namespace Humans.Application.Interfaces.Mailer.Dtos;

/// <summary>
/// The classification of one ML subscriber after the matching ladder and the
/// marketing-pref matrix evaluation. Each verified-match decision is bucketed
/// at plan time so the preview shows the actual write that will happen.
/// </summary>
public enum SubscriberOutcome
{
    UnconfirmedSkipped,
    AmbiguousMultipleVerified,

    /// <summary>No Humans match — create a new human from the ML subscriber.</summary>
    CreateNewHuman,

    /// <summary>Humans has the email but unverified — delete that row and create a fresh user.</summary>
    ReplaceUnverifiedEmail,

    /// <summary>Verified human match, Marketing pref already matches ML — no-op.</summary>
    VerifiedPrefsAlreadyMatch,

    /// <summary>Verified human match, ML opt-in differs from Humans — flip to opt-in.</summary>
    VerifiedFlipToOptIn,

    /// <summary>Verified human match, ML opt-out differs from Humans — flip to opt-out.</summary>
    VerifiedFlipToOptOut,

    /// <summary>Verified human match, user touched Humans' pref more recently than ML — keep Humans' state.</summary>
    VerifiedKeepHumansPref,
}

public sealed record SubscriberDecision(
    string Email,
    string Status,
    SubscriberOutcome Outcome,
    Guid? TargetUserId,
    Guid? UnverifiedEmailIdToDelete,
    IReadOnlyList<Guid>? AmbiguousUserIds);
