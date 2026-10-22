namespace Humans.MailerLite.Services.Dtos;

/// <summary>Bucketed at plan time so the preview shows the write that will happen.</summary>
internal enum SubscriberOutcome
{
    UnconfirmedSkipped,
    /// <summary>Several verified owners or several unverified matching rows: skipped.</summary>
    AmbiguousEmailMatches,

    CreateNewHuman,

    /// <summary>Delete the unverified email row and create a fresh user.</summary>
    ReplaceUnverifiedEmail,

    VerifiedPrefsAlreadyMatch,

    VerifiedFlipToOptIn,

    VerifiedFlipToOptOut,

    /// <summary>The member changed the preference in Humans more recently than in MailerLite.</summary>
    VerifiedKeepHumansPref,

    /// <summary>
    /// GDPR remediation: an opt-in set by the erroneous whole-account import, on someone outside the Website group
    /// with no prior consent. The row is deleted (back to null, not opt-out). Found from the Humans side.
    /// </summary>
    ResetMarketingFlag,
}

internal sealed record SubscriberDecision(
    string Email,
    string Status,
    SubscriberOutcome Outcome,
    Guid? TargetUserId,
    Guid? UnverifiedEmailIdToDelete,
    IReadOnlyList<Guid>? AmbiguousUserIds);
