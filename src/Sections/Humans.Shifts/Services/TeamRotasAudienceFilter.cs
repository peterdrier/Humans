using Humans.Shifts.Contracts;

namespace Humans.Shifts.Services;

/// <summary>
/// The period flags apply only when <see cref="UpcomingOnly"/> is false: the form hides them under "upcoming", so
/// whatever the hidden boxes hold must not filter an upcoming send.
/// </summary>
internal sealed record TeamRotasAudienceFilter(
    bool UpcomingOnly,
    bool IncludeBuild,
    bool IncludeEvent,
    bool IncludeStrike)
{
    internal static TeamRotasAudienceFilter Default { get; } = new(true, true, true, true);

    /// <summary>Posted back with the send so a selection changed since the preview is re-previewed, not sent.</summary>
    internal string Key =>
        $"{(UpcomingOnly ? 1 : 0)}{(IncludeBuild ? 1 : 0)}{(IncludeEvent ? 1 : 0)}{(IncludeStrike ? 1 : 0)}";

    internal bool Includes(RotaPeriod period) => UpcomingOnly || period switch
    {
        RotaPeriod.Build => IncludeBuild,
        RotaPeriod.Event => IncludeEvent,
        RotaPeriod.Strike => IncludeStrike,
        // An all-event rota belongs to every period.
        _ => IncludeBuild || IncludeEvent || IncludeStrike,
    };
}
