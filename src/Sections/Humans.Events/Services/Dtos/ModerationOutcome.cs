namespace Humans.Events.Services.Dtos;

/// <summary>
/// Result of a moderation decision. The two refusals are expected moderator
/// outcomes; anything else (missing event, data faults) throws.
/// </summary>
internal enum ModerationOutcome
{
    Applied,
    ReasonRequired,
    NotPending
}
