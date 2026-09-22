using Humans.Shifts.Contracts;

namespace Humans.Shifts.Services;

/// <summary>
/// Narrows the audience of the team-wide coordinator message. <see cref="UpcomingOnly"/>
/// keeps the original behaviour (rotas with at least one shift not yet ended); clearing it
/// opens the whole active event — a post-event thank-you — and the three period flags then
/// pick which of the event's rotas count.
/// </summary>
/// <remarks>
/// The period flags only apply once <see cref="UpcomingOnly"/> is false: the compose form
/// hides them under "upcoming", so an upcoming-scoped send must not be filtered by whatever
/// those hidden boxes happen to hold.
/// </remarks>
internal sealed record TeamRotasAudienceFilter(
    bool UpcomingOnly,
    bool IncludeBuild,
    bool IncludeEvent,
    bool IncludeStrike)
{
    /// <summary>The compose form's defaults: upcoming rotas, every period.</summary>
    internal static TeamRotasAudienceFilter Default { get; } = new(true, true, true, true);

    internal bool Includes(RotaPeriod period) => UpcomingOnly || period switch
    {
        RotaPeriod.Build => IncludeBuild,
        RotaPeriod.Event => IncludeEvent,
        RotaPeriod.Strike => IncludeStrike,
        // A rota the coordinator marked as spanning the whole event belongs to
        // every period, so any selected period pulls it in.
        _ => IncludeBuild || IncludeEvent || IncludeStrike,
    };
}
