using Humans.Base.Interfaces;
using Humans.Governance.Contracts;
using Humans.Governance.Data;
using Humans.Governance.Domain;
using Humans.Governance.Services.Dtos;
using Humans.Google.Contracts;
using NodaTime;

namespace Humans.Governance.Services;

internal sealed class AssemblyVoteDrafts(
    IAssemblyVoteRepository repository,
    IGoogleTranslationService translation,
    IClock clock,
    ILogger<AssemblyVoteService> logger) : IApplicationService
{
    // Bounded text columns, mirrored from the EF configurations: form text is checked with Fits
    // so an over-long value is a rejected draft rather than a 500. Title's jsonb column is
    // uncapped, but it is copied into bounded email and notification columns after an
    // irreversible transition, so it is bounded at the draft while the vote is still editable.
    internal const int MaxTitleLength = 200;
    internal const int MaxOptionKeyLength = 100;
    internal const int MaxInfoUrlLength = 2000;
    internal const int MaxCultureLength = 10;

    internal static bool Fits(string? value, int max) => (value?.Length ?? 0) <= max;

    public async Task<AssemblyVoteDraft?> GetDraftAsync(Guid voteId, CancellationToken ct = default)
    {
        var vote = await repository.GetByIdAsync(voteId, ct);
        if (vote is null) return null;

        return new AssemblyVoteDraft(
            vote.Title.Values.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase),
            vote.OfficialText.Values.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase),
            vote.OfficialCulture,
            vote.InfoUrl,
            vote.Kind,
            vote.RequiredMajority,
            vote.IndicativeAudience,
            vote.BallotDisclosure,
            vote.AssemblyDate,
            vote.ClosesAt,
            vote.Options
                .Select(o => new AssemblyVoteDraftOption(
                    o.Key, o.Order, o.Label.Values.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase)))
                .ToList());
    }

    public async Task<Guid?> CreateDraftAsync(
        AssemblyVoteDraft draft, Guid actorUserId, CancellationToken ct = default)
    {
        if (!IsDraftValid(draft)) return null;

        var now = clock.GetCurrentInstant();
        var vote = new AssemblyVote
        {
            Id = Guid.NewGuid(),
            CreatedByUserId = actorUserId,
            CreatedAt = now
        };

        ApplyDraft(vote, draft, now);
        foreach (var option in OptionsFor(vote.Id, draft))
        {
            vote.Options.Add(option);
        }

        await repository.AddAsync(vote, ct);
        return vote.Id;
    }

    public async Task<AssemblyVoteActionResult> UpdateDraftAsync(
        Guid voteId, AssemblyVoteDraft draft, Guid actorUserId, CancellationToken ct = default)
    {
        var vote = await repository.GetByIdAsync(voteId, ct);
        if (vote is null) return AssemblyVoteActionResult.NotFound;

        // Content is immutable once the electorate has been told what it is voting on.
        if (vote.Status != AssemblyVoteStatus.Draft) return AssemblyVoteActionResult.WrongState;
        if (!IsDraftValid(draft)) return AssemblyVoteActionResult.Invalid;

        ApplyDraft(vote, draft, clock.GetCurrentInstant());

        // Refused means the vote opened between the read above and this write, and content is
        // immutable from that moment — the same answer the status check above would have given.
        if (!await repository.ReplaceOptionsAsync(vote, OptionsFor(vote.Id, draft), ct))
            return AssemblyVoteActionResult.WrongState;

        return AssemblyVoteActionResult.Ok;
    }

    public async Task<int> PreFillTranslationsAsync(
        Guid voteId, IReadOnlyList<string> targetCultures, Guid actorUserId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(targetCultures);

        var vote = await repository.GetByIdAsync(voteId, ct);
        if (vote is null) return 0;
        // Draft-only, same reason content is: translating a motion the electorate is already
        // voting on would change what some members read mid-vote.
        if (vote.Status != AssemblyVoteStatus.Draft) return 0;

        // The revision being translated. Google's round-trip is long enough for the Board to
        // edit the draft in between, and this write carries every authored field.
        var readAt = vote.UpdatedAt;

        var source = vote.OfficialCulture;

        // Every authored text as a mutable bag, so one batched call per culture fills them all.
        var title = Copy(vote.Title);
        var officialText = Copy(vote.OfficialText);
        var options = vote.Options.Select(o => new { Option = o, Label = Copy(o.Label) }).ToList();

        List<Dictionary<string, string>> all = [title, officialText, .. options.Select(o => o.Label)];

        var filled = 0;
        foreach (var target in targetCultures.Where(
                     t => !string.Equals(t, source, StringComparison.OrdinalIgnoreCase)))
        {
            // Blanks only. Authored text is never overwritten — the Board's own wording in any
            // culture outranks a machine's, and the official culture stays the binding version.
            var pending = all.Where(d => HasText(d, source) && !HasText(d, target)).ToList();
            if (pending.Count == 0) continue;

            var translated = await translation.TranslateAsync(
                [.. pending.Select(d => d[source])], source, target, ct);
            for (var i = 0; i < pending.Count; i++)
            {
                pending[i][target] = translated[i];
            }

            filled += pending.Count;
        }

        if (filled == 0) return 0;

        vote.Title = new GovernanceLocalizedText(title);
        vote.OfficialText = new GovernanceLocalizedText(officialText);
        foreach (var o in options)
        {
            o.Option.Label = new GovernanceLocalizedText(o.Label);
        }

        vote.UpdatedAt = clock.GetCurrentInstant();

        // Refused means the draft moved on while the translations were being fetched — it
        // opened, or somebody edited it. Nothing was stored, so nothing was filled.
        if (!await repository.UpdateAsync(vote, AssemblyVoteStatus.Draft, readAt, ct)) return 0;

        // Not audited, like the rest of draft authoring: a draft has no legal effect and the
        // Board can still rewrite every word. Open audits the content the electorate gets.
        logger.LogInformation(
            "Assembly vote {VoteId}: pre-filled {Count} missing translations from {Source} for {ActorUserId}",
            vote.Id, filled, source, actorUserId);

        return filled;

        static Dictionary<string, string> Copy(GovernanceLocalizedText text) =>
            new(text.Values, StringComparer.OrdinalIgnoreCase);

        static bool HasText(Dictionary<string, string> values, string culture) =>
            values.TryGetValue(culture, out var v) && !string.IsNullOrWhiteSpace(v);
    }

    public async Task<AssemblyVoteActionResult> DeleteDraftAsync(
        Guid voteId, Guid actorUserId, CancellationToken ct = default)
    {
        var vote = await repository.GetByIdAsync(voteId, ct);
        if (vote is null) return AssemblyVoteActionResult.NotFound;
        if (vote.Status != AssemblyVoteStatus.Draft) return AssemblyVoteActionResult.WrongState;

        // Refused means the vote opened between the read above and this delete, and an open
        // vote is never deleted — the same answer the status check above would have given.
        if (!await repository.DeleteAsync(voteId, ct)) return AssemblyVoteActionResult.WrongState;

        return AssemblyVoteActionResult.Ok;
    }

    /// <summary>
    /// A draft is publishable when it has a title and binding text in its official culture,
    /// a future closing time, and — for a ranked vote — at least two distinctly-keyed options,
    /// each labelled in the official culture. The label is what a voter reads on the ballot;
    /// a keyed-but-unlabelled option would open as a blank line on a binding vote.
    /// </summary>
    private bool IsDraftValid(AssemblyVoteDraft draft)
    {
        // An out-of-range numeric enum binds silently and falls through to the wrong default — an
        // undefined Kind opens with no options yet counts as instant-runoff. Content locks at open.
        if (!Enum.IsDefined(draft.Kind)) return false;
        if (!Enum.IsDefined(draft.RequiredMajority)) return false;
        if (!Enum.IsDefined(draft.IndicativeAudience)) return false;
        if (!Enum.IsDefined(draft.BallotDisclosure)) return false;
        if (string.IsNullOrWhiteSpace(draft.OfficialCulture)) return false;
        if (!Fits(draft.OfficialCulture, MaxCultureLength)) return false;
        if (!Fits(draft.InfoUrl, MaxInfoUrlLength)) return false;
        if (!string.IsNullOrWhiteSpace(draft.InfoUrl) && SafeInfoUrl(draft.InfoUrl) is null) return false;
        if (!draft.Title.TryGetValue(draft.OfficialCulture, out var title)
            || string.IsNullOrWhiteSpace(title)) return false;
        if (draft.Title.Values.Any(t => !Fits(t, MaxTitleLength))) return false;
        if (!draft.OfficialText.TryGetValue(draft.OfficialCulture, out var text)
            || string.IsNullOrWhiteSpace(text)) return false;
        if (draft.ClosesAt <= clock.GetCurrentInstant()) return false;

        if (draft.Kind != AssemblyVoteKind.RankedChoice) return true;

        // A 2/3 threshold has no defined meaning under instant runoff, and the acta would claim a
        // threshold the count never applied. Qualified majorities are asked as YesNo questions.
        if (draft.RequiredMajority != RequiredMajority.Simple) return false;

        return draft.Options.Count >= 2
               && draft.Options.All(o => !string.IsNullOrWhiteSpace(o.Key)
                                         && Fits(o.Key, MaxOptionKeyLength))
               && draft.Options.All(o => o.Label.TryGetValue(draft.OfficialCulture, out var label)
                                         && !string.IsNullOrWhiteSpace(label))
               && draft.Options.Select(o => o.Key).Distinct(StringComparer.Ordinal).Count()
                  == draft.Options.Count;
    }

    // Used on reads too: older rows may predate URL validation.
    internal static string? SafeInfoUrl(string? url)
    {
        var trimmed = url?.Trim();
        return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https" ? trimmed : null;
    }

    private static void ApplyDraft(AssemblyVote vote, AssemblyVoteDraft draft, Instant now)
    {
        vote.Title = new GovernanceLocalizedText(draft.Title.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase));
        vote.OfficialText = new GovernanceLocalizedText(
            draft.OfficialText.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase));
        vote.OfficialCulture = draft.OfficialCulture;
        vote.InfoUrl = SafeInfoUrl(draft.InfoUrl);
        vote.Kind = draft.Kind;
        vote.RequiredMajority = draft.RequiredMajority;
        vote.IndicativeAudience = draft.IndicativeAudience;
        vote.BallotDisclosure = draft.BallotDisclosure;
        vote.AssemblyDate = draft.AssemblyDate;
        vote.ClosesAt = draft.ClosesAt;
        vote.UpdatedAt = now;
    }

    private static List<AssemblyVoteOption> OptionsFor(Guid voteId, AssemblyVoteDraft draft) =>
        draft.Kind != AssemblyVoteKind.RankedChoice
            ? []
            : draft.Options
                .Select(o => new AssemblyVoteOption
                {
                    Id = Guid.NewGuid(),
                    VoteId = voteId,
                    Order = o.Order,
                    Key = o.Key,
                    Label = new GovernanceLocalizedText(o.Label.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase))
                })
                .ToList();
}
