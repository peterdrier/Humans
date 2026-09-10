using Humans.Base.Extensions;
using Humans.Governance.Domain;
using Humans.Governance.Services.Dtos;
using NodaTime;

namespace Humans.Governance.Models;

// ==========================================================================
// Member-facing
// ==========================================================================

/// <summary>Votes list page. The service already orders open votes first.</summary>
internal sealed class AssemblyVoteListViewModel
{
    public IReadOnlyList<AssemblyVoteListItem> Votes { get; init; } = [];
}

/// <summary>
/// One vote's page: the service DTO carries everything a viewer is allowed to see (text, stats,
/// own ballot); this only adds the RankedChoice ballot-form scaffolding, which the DTO has no
/// reason to carry since it is form state, not vote content.
/// </summary>
internal sealed class AssemblyVoteDetailViewModel
{
    public required AssemblyVoteDetail Vote { get; init; }

    /// <summary>One row per option, pre-filled from the viewer's standing ballot when they have one. Empty for a YesNo vote.</summary>
    public IReadOnlyList<AssemblyRankedBallotOptionRow> RankedOptions { get; init; } = [];
}

/// <summary>
/// One option row of a posted ranked ballot: mirrors Surveys' select-per-row pattern
/// (<c>_SurveyQuestions.cshtml</c>) — <see cref="Selection"/> is the rank typed into that row's
/// select, or null when left unranked.
/// </summary>
internal sealed class AssemblyRankedBallotOptionRow
{
    public string OptionKey { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public int? Selection { get; set; }
}

/// <summary>
/// Posted ballot form: a YesNo choice, or — for RankedChoice votes — a rank per option.
/// <see cref="Choice"/> selects which; the controller reads <see cref="RankedOptions"/> only
/// when it is <see cref="AssemblyBallotChoice.Ranked"/>.
/// </summary>
internal sealed class AssemblyBallotFormViewModel
{
    public Guid VoteId { get; set; }
    public AssemblyBallotChoice Choice { get; set; }
    public List<AssemblyRankedBallotOptionRow> RankedOptions { get; set; } = [];

    /// <summary>
    /// True when two options were given the same rank. Projecting to keys would erase that —
    /// two rows ranked 1 sort into two distinct keys, which the service's duplicate-key check
    /// cannot see — and the ballot would silently be counted in form-row order. On a binding
    /// vote an ambiguous ballot is rejected, never guessed at.
    /// </summary>
    public bool HasDuplicateRanks()
    {
        var ranks = RankedOptions
            .Where(r => r.Selection is > 0 && !string.IsNullOrWhiteSpace(r.OptionKey))
            .Select(r => r.Selection!.Value)
            .ToList();

        return ranks.Count != ranks.Distinct().Count();
    }

    /// <summary>
    /// The ordered option keys <c>CastBallotAsync</c> expects: rows with a positive rank,
    /// ascending, blanks dropped. Only meaningful once <see cref="HasDuplicateRanks"/> is
    /// false — the caller checks that first.
    /// </summary>
    public IReadOnlyList<string>? ToRanking()
    {
        if (Choice != AssemblyBallotChoice.Ranked) return null;

        return RankedOptions
            .Where(r => r.Selection is > 0 && !string.IsNullOrWhiteSpace(r.OptionKey))
            .OrderBy(r => r.Selection)
            .Select(r => r.OptionKey)
            .ToList();
    }
}

/// <summary>Results page after close: the stored tally, peeks, acta block and disclosed ballots — all carried on the DTO as-is.</summary>
internal sealed class AssemblyVoteResultsViewModel
{
    public required AssemblyVoteResultsView Results { get; init; }
}

// ==========================================================================
// Admin
// ==========================================================================

/// <summary>Admin list: every vote in every state.</summary>
internal sealed class AssemblyVoteAdminListViewModel
{
    public IReadOnlyList<AssemblyVoteListItem> Votes { get; init; } = [];
}

/// <summary>
/// Draft create/edit form. Carries a per-culture input for every localized field so the Board
/// can author all six cultures in one page, plus the repeatable RankedChoice option rows.
/// </summary>
internal sealed class AssemblyVoteDraftFormViewModel
{
    /// <summary>Association's timezone for <see cref="ClosesAt"/> — every closing time is authored in Madrid local time (Docs/features/assembly-votes.md).</summary>
    private static readonly DateTimeZone MadridZone = DateTimeZoneProviders.Tzdb["Europe/Madrid"];

    /// <summary>Null on the create form; set on edit.</summary>
    public Guid? VoteId { get; set; }

    public Dictionary<string, string> Title { get; set; } = [];
    public Dictionary<string, string> OfficialText { get; set; } = [];
    public string OfficialCulture { get; set; } = CultureCatalog.DefaultCultureCode;
    public string? InfoUrl { get; set; }
    public AssemblyVoteKind Kind { get; set; }
    public RequiredMajority RequiredMajority { get; set; }
    public IndicativeAudience IndicativeAudience { get; set; }
    public BallotDisclosure BallotDisclosure { get; set; }
    public DateTime? AssemblyDate { get; set; }

    /// <summary>Posted as Europe/Madrid local time; converted to an <see cref="Instant"/> in <see cref="ToDraft"/>.</summary>
    public DateTime ClosesAt { get; set; }

    /// <summary>RankedChoice options only; ignored for a YesNo vote (fixed Yes/No/Abstain).</summary>
    public List<AssemblyVoteDraftOptionFormRow> Options { get; set; } = [];

    public IReadOnlyList<string> Cultures { get; } = CultureCatalog.SupportedCultureCodes;

    /// <summary>Builds the service DTO from the posted form. Authored order is the row order posted.</summary>
    public AssemblyVoteDraft ToDraft()
    {
        return new AssemblyVoteDraft(
            Title,
            OfficialText,
            OfficialCulture,
            string.IsNullOrWhiteSpace(InfoUrl) ? null : InfoUrl,
            Kind,
            RequiredMajority,
            IndicativeAudience,
            BallotDisclosure,
            AssemblyDate is { } assemblyDate ? LocalDate.FromDateTime(assemblyDate) : null,
            LocalDateTime.FromDateTime(ClosesAt).InZoneLeniently(MadridZone).ToInstant(),
            Options
                .Select((row, order) => new AssemblyVoteDraftOption(row.Key, order, row.Label))
                .ToList());
    }

    /// <summary>Rehydrates the form from a stored draft, for the Edit GET.</summary>
    public static AssemblyVoteDraftFormViewModel FromDraft(Guid voteId, AssemblyVoteDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);

        return new AssemblyVoteDraftFormViewModel
        {
            VoteId = voteId,
            Title = new Dictionary<string, string>(draft.Title, StringComparer.OrdinalIgnoreCase),
            OfficialText = new Dictionary<string, string>(draft.OfficialText, StringComparer.OrdinalIgnoreCase),
            OfficialCulture = draft.OfficialCulture,
            InfoUrl = draft.InfoUrl,
            Kind = draft.Kind,
            RequiredMajority = draft.RequiredMajority,
            IndicativeAudience = draft.IndicativeAudience,
            BallotDisclosure = draft.BallotDisclosure,
            AssemblyDate = draft.AssemblyDate?.ToDateTimeUnspecified(),
            ClosesAt = draft.ClosesAt.InZone(MadridZone).ToDateTimeUnspecified(),
            Options = draft.Options
                .OrderBy(o => o.Order)
                .Select(o => new AssemblyVoteDraftOptionFormRow
                {
                    Key = o.Key,
                    Label = new Dictionary<string, string>(o.Label, StringComparer.OrdinalIgnoreCase)
                })
                .ToList()
        };
    }
}

/// <summary>One repeatable RankedChoice option row on the draft form: a stable key plus a per-culture label.</summary>
internal sealed class AssemblyVoteDraftOptionFormRow
{
    public string Key { get; set; } = string.Empty;
    public Dictionary<string, string> Label { get; set; } = [];
}

/// <summary>Admin Peek: the live tally, rendered exactly as the results page would, before close.</summary>
internal sealed class AssemblyVotePeekViewModel
{
    public Guid VoteId { get; init; }
    public required AssemblyVoteResult Result { get; init; }

    /// <summary>
    /// The authored options, so the rounds table can print labels instead of the raw keys the
    /// counting result is keyed by. An Admin peeking mid-assembly has to read this out loud.
    /// </summary>
    public IReadOnlyList<AssemblyVoteOptionView> Options { get; init; } = [];

    /// <summary>The option's label, falling back to its key when the option is gone.</summary>
    public string LabelFor(string key) =>
        Options.FirstOrDefault(o => string.Equals(o.Key, key, StringComparison.Ordinal))?.Label ?? key;
}

/// <summary>Admin/Board ballots list on a closed vote.</summary>
internal sealed class AssemblyVoteBallotsViewModel
{
    public Guid VoteId { get; init; }
    public IReadOnlyList<AssemblyBallotDisclosureRow> Ballots { get; init; } = [];
}

/// <summary>Posted Extend action: the new closing time, Europe/Madrid local.</summary>
internal sealed class AssemblyVoteExtendFormModel
{
    private static readonly DateTimeZone MadridZone = DateTimeZoneProviders.Tzdb["Europe/Madrid"];

    public Guid VoteId { get; set; }
    public DateTime NewClosesAt { get; set; }

    public Instant ToInstant() =>
        LocalDateTime.FromDateTime(NewClosesAt).InZoneLeniently(MadridZone).ToInstant();
}

/// <summary>Posted Cancel action: a required reason (statutes Art. 8.2 exit).</summary>
internal sealed class AssemblyVoteCancelFormModel
{
    public Guid VoteId { get; set; }
    public string Reason { get; set; } = string.Empty;
}
