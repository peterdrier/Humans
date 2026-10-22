namespace Humans.Events.Services.Dtos;

/// <summary>The two refusals are expected outcomes; anything else (missing event, data faults) throws.</summary>
internal enum ModerationOutcome
{
    Applied,
    ReasonRequired,
    NotPending
}
