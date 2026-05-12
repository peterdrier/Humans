namespace Humans.Application.Interfaces.Mailer.Dtos;

/// <summary>
/// The classification of one ML subscriber after the matching ladder.
/// Order mirrors spec §5.
/// </summary>
public enum SubscriberOutcome
{
    UnconfirmedSkipped,
    AttachVerified,
    AttachVerifiedConfirmOnly,    // pref already matches; no flip needed
    AttachVerifiedConflictKept,   // Humans state wins per conflict rule
    DeleteUnverifiedThenCreate,
    CreateContact,
    AmbiguousMultipleVerified,
}

public sealed record SubscriberDecision(
    string Email,
    string Status,
    SubscriberOutcome Outcome,
    Guid? TargetUserId,
    Guid? UnverifiedEmailIdToDelete,
    IReadOnlyList<Guid>? AmbiguousUserIds);
