using Humans.Application.Extensions;
using Humans.Application.Interfaces.Surveys;
using Humans.Domain.Enums;
using Humans.Domain.ValueObjects;
using Humans.Web.Extensions;
using NodaTime;
using NodaTime.Text;

namespace Humans.Web.Models;

/// <summary>Admin index: the survey list (sorting done in the controller).</summary>
public sealed class SurveyAdminIndexViewModel
{
    public IReadOnlyList<SurveySummary> Surveys { get; init; } = [];
}

/// <summary>A team choice for the audience picker.</summary>
public sealed record SurveyTeamOption(Guid Id, string Name);

// ── Answering wizard (entry) ────────────────────────────────────────────────

/// <summary>
/// The wizard intro/privacy step: survey title/intro resolved for display, the transparency note,
/// a language picker, and (only when the survey allows it) the anonymity-tier selector. Carries the
/// token through to the Start POST.
/// </summary>
public sealed class SurveyIntroViewModel
{
    public string Token { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Intro { get; init; } = string.Empty;

    /// <summary>The currently-selected language; the form posts it back so the wizard runs in it.</summary>
    public string Culture { get; init; } = CultureCatalog.DefaultCultureCode;

    /// <summary>When true, the three-tier anonymity selector is shown; otherwise only Identified applies.</summary>
    public bool AllowAnonymous { get; init; }

    /// <summary>
    /// When true the anonymity-tier radios are rendered (invited path with <see cref="AllowAnonymous"/>);
    /// the public-slug path sets this false — that path is always Anonymous, so only the language picker shows.
    /// </summary>
    public bool ShowAnonymitySelector { get; init; }

    /// <summary>True on the public-slug path: the intro form posts to <c>Public/Start</c> with <see cref="Slug"/> instead of <c>Answer/Start</c> with the token.</summary>
    public bool IsPublic { get; init; }

    /// <summary>The public slug (only set when <see cref="IsPublic"/>); drives the start route on the public path.</summary>
    public string Slug { get; init; } = string.Empty;

    /// <summary>True when the invitee already has answers in progress (Identified resume).</summary>
    public bool HasResumableDraft { get; init; }

    public IReadOnlyList<string> Cultures { get; } = CultureCatalog.SupportedCultureCodes;
}

/// <summary>Posted by the intro form to begin the wizard.</summary>
public sealed class SurveyStartViewModel
{
    public string Token { get; set; } = string.Empty;
    public ResponseAnonymity Anonymity { get; set; } = ResponseAnonymity.Identified;
    public string Culture { get; set; } = CultureCatalog.DefaultCultureCode;
}

/// <summary>Closed / invalid-link page. <see cref="Reason"/> is a friendly explanation key.</summary>
public sealed class SurveyClosedViewModel
{
    public string? Reason { get; init; }
}

/// <summary>Admin Send page: header (title/status/audience), resolved audience size, and per-invite status rows (sorted in the controller).</summary>
public sealed class SurveySendViewModel
{
    public Guid Id { get; init; }
    public string Title { get; init; } = string.Empty;
    public SurveyStatus Status { get; init; }
    public SurveyAudienceType? AudienceType { get; init; }
    public int PreviewCount { get; init; }
    public IReadOnlyList<SurveyInviteStatus> Invitations { get; init; } = [];

    public bool CanSend => Status == SurveyStatus.Open && AudienceType is not null;
}

/// <summary>
/// Renders one input per supported culture for a localized field. <paramref name="NamePrefix"/> is the
/// bound name without the culture key (e.g. <c>Title</c> or <c>Questions[k].Prompt</c>); the partial
/// emits <c>NamePrefix[culture]</c>.
/// </summary>
public sealed record SurveyLocalizedFieldModel(
    string NamePrefix,
    string Label,
    IReadOnlyDictionary<string, string> Values,
    bool Multiline = false);

/// <summary>One question card in the builder. <paramref name="Key"/> is the non-sequential indexer key (or the <c>__QKEY__</c> placeholder in the JS template).</summary>
public sealed record SurveyQuestionCardModel(string Key, SurveyQuestionBuilderViewModel Question);

/// <summary>One option row in the builder. Keys are non-sequential indexers (or <c>__QKEY__</c>/<c>__OKEY__</c> placeholders in templates).</summary>
public sealed record SurveyOptionRowModel(string QuestionKey, string OptionKey, SurveyOptionBuilderViewModel Option);

/// <summary>One show-if clause row in the builder. Keys are non-sequential indexers (or <c>__QKEY__</c>/<c>__CKEY__</c> placeholders in templates).</summary>
public sealed record SurveyBranchClauseRowModel(string QuestionKey, string ClauseKey, SurveyBranchClauseBuilderViewModel Clause);

/// <summary>
/// The full survey builder form. Localized fields bind per culture (<c>Title[en]</c>, …); questions,
/// options and show-if clauses use non-sequential indexers (a <c>.Index</c> hidden field per
/// collection) so rows can be added/removed client-side without renumbering. Show-if conditions are
/// posted as structured clause rows (question/operator/option values) and the resulting
/// <see cref="BranchCondition"/> is validated server-side on save. New questions get their
/// <see cref="SurveyQuestionBuilderViewModel.Id"/> pre-assigned client-side so clauses can reference
/// questions that haven't been saved yet (the service honours supplied ids).
/// </summary>
public sealed class SurveyBuilderViewModel
{
    public Guid? Id { get; set; }
    public SurveyStatus Status { get; set; } = SurveyStatus.Draft;

    public Dictionary<string, string> Title { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Intro { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> ThankYou { get; set; } = new(StringComparer.Ordinal);

    public string DefaultCulture { get; set; } = CultureCatalog.DefaultCultureCode;
    public bool AllowAnonymous { get; set; }
    public LocalDateTime? OpensAt { get; set; }
    public LocalDateTime? ClosesAt { get; set; }
    public SurveyAudienceType? AudienceType { get; set; }
    public Guid? AudienceTeamId { get; set; }
    public LocalDate? AudienceLoggedInSince { get; set; }
    public string? PublicSlug { get; set; }

    public List<SurveyQuestionBuilderViewModel> Questions { get; set; } = [];

    // Render-only (not bound back).
    public IReadOnlyList<string> Cultures { get; } = CultureCatalog.SupportedCultureCodes;
    public IReadOnlyList<SurveyTeamOption> Teams { get; set; } = [];

    public bool IsNew => Id is null;

    // datetime-local <input> values (empty when unset).
    public string OpensAtInput => FormatLocal(OpensAt);
    public string ClosesAtInput => FormatLocal(ClosesAt);

    // date <input> value (empty when unset).
    public string AudienceLoggedInSinceInput
        => AudienceLoggedInSince is null ? string.Empty : LocalDatePattern.Iso.Format(AudienceLoggedInSince.Value);

    private static string FormatLocal(LocalDateTime? local)
        => local is null ? string.Empty : DateFormattingExtensions.PlacementDateTimePattern.Format(local.Value);

    /// <summary>Maps the posted form to the service authoring input. Question/option order is the posted order.</summary>
    public SurveyEditInput ToEditInput(DateTimeZone zone) => new(
        new LocalizedText(Title),
        new LocalizedText(Intro),
        new LocalizedText(ThankYou),
        string.IsNullOrWhiteSpace(DefaultCulture) ? CultureCatalog.DefaultCultureCode : DefaultCulture,
        AllowAnonymous,
        ToInstant(OpensAt, zone),
        ToInstant(ClosesAt, zone),
        AudienceType,
        AudienceType == SurveyAudienceType.Team ? AudienceTeamId : null,
        AudienceType == SurveyAudienceType.LoggedInSince ? ToStartOfDayInstant(AudienceLoggedInSince, zone) : null,
        string.IsNullOrWhiteSpace(PublicSlug) ? null : PublicSlug,
        Questions.Select((q, i) => q.ToInput(i)).ToList());

    public static SurveyBuilderViewModel FromDetail(SurveyDetail detail, IReadOnlyList<SurveyTeamOption> teams, DateTimeZone zone)
    {
        var e = detail.Editable;
        return new SurveyBuilderViewModel
        {
            Id = detail.Id,
            Status = detail.Status,
            Title = ToDict(e.Title),
            Intro = ToDict(e.Intro),
            ThankYou = ToDict(e.ThankYou),
            DefaultCulture = e.DefaultCulture,
            AllowAnonymous = e.AllowAnonymous,
            OpensAt = FromInstant(e.OpensAt, zone),
            ClosesAt = FromInstant(e.ClosesAt, zone),
            AudienceType = e.AudienceType,
            AudienceTeamId = e.AudienceTeamId,
            AudienceLoggedInSince = e.AudienceLoggedInSince?.InZone(zone).Date,
            PublicSlug = e.PublicSlug,
            Questions = e.Questions.Select(SurveyQuestionBuilderViewModel.FromInput).ToList(),
            Teams = teams,
        };
    }

    internal static Dictionary<string, string> ToDict(LocalizedText text)
        => new(text.Values, StringComparer.Ordinal);

    internal static Instant? ToInstant(LocalDateTime? local, DateTimeZone zone)
        => local?.InZoneLeniently(zone).ToInstant();

    internal static Instant? ToStartOfDayInstant(LocalDate? date, DateTimeZone zone)
        => date?.AtStartOfDayInZone(zone).ToInstant();

    internal static LocalDateTime? FromInstant(Instant? instant, DateTimeZone zone)
        => instant?.InZone(zone).LocalDateTime;
}

public sealed class SurveyQuestionBuilderViewModel
{
    public Guid? Id { get; set; }
    public int PageNumber { get; set; } = 1;
    public SurveyQuestionType Type { get; set; } = SurveyQuestionType.SingleChoice;
    public Dictionary<string, string> Prompt { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> HelpText { get; set; } = new(StringComparer.Ordinal);
    public bool IsRequired { get; set; }
    public int? RatingMin { get; set; }
    public int? RatingMax { get; set; }
    public Dictionary<string, string> RatingMinLabel { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> RatingMaxLabel { get; set; } = new(StringComparer.Ordinal);
    public BranchCombine ShowIfCombine { get; set; } = BranchCombine.All;
    public List<SurveyBranchClauseBuilderViewModel> ShowIfClauses { get; set; } = [];
    public List<SurveyOptionBuilderViewModel> Options { get; set; } = [];

    public QuestionInput ToInput(int order) => new(
        Id,
        PageNumber <= 0 ? 1 : PageNumber,
        order,
        Type,
        new LocalizedText(Prompt),
        new LocalizedText(HelpText),
        IsRequired,
        RatingMin,
        RatingMax,
        new LocalizedText(RatingMinLabel),
        new LocalizedText(RatingMaxLabel),
        ToShowIf(),
        Options.Select((o, i) => o.ToInput(i)).ToList());

    public static SurveyQuestionBuilderViewModel FromInput(QuestionInput q) => new()
    {
        Id = q.Id,
        PageNumber = q.PageNumber,
        Type = q.Type,
        Prompt = SurveyBuilderViewModel.ToDict(q.Prompt),
        HelpText = SurveyBuilderViewModel.ToDict(q.HelpText),
        IsRequired = q.IsRequired,
        RatingMin = q.RatingMin,
        RatingMax = q.RatingMax,
        RatingMinLabel = SurveyBuilderViewModel.ToDict(q.RatingMinLabel),
        RatingMaxLabel = SurveyBuilderViewModel.ToDict(q.RatingMaxLabel),
        ShowIfCombine = q.ShowIf?.Combine ?? BranchCombine.All,
        ShowIfClauses = q.ShowIf?.Clauses.Select(SurveyBranchClauseBuilderViewModel.FromClause).ToList() ?? [],
        Options = q.Options.Select(SurveyOptionBuilderViewModel.FromInput).ToList(),
    };

    /// <summary>Clauses without a target question (never picked / target removed) are dropped; no clauses ⇒ always visible.</summary>
    private BranchCondition? ToShowIf()
    {
        var clauses = ShowIfClauses
            .Where(c => c.QuestionId is not null)
            .Select(c => c.ToClause())
            .ToList();
        return clauses.Count == 0 ? null : new BranchCondition { Combine = ShowIfCombine, Clauses = clauses };
    }
}

/// <summary>One structured show-if clause: an earlier question, an operator, and (for Is/IsNot) the option values to match.</summary>
public sealed class SurveyBranchClauseBuilderViewModel
{
    public Guid? QuestionId { get; set; }
    public BranchOperator Operator { get; set; } = BranchOperator.Is;
    public List<string> OptionValues { get; set; } = [];

    public BranchClause ToClause() => new()
    {
        QuestionId = QuestionId!.Value,
        Operator = Operator,
        OptionValues = OptionValues.Where(v => !string.IsNullOrWhiteSpace(v)).ToList(),
    };

    public static SurveyBranchClauseBuilderViewModel FromClause(BranchClause c) => new()
    {
        QuestionId = c.QuestionId,
        Operator = c.Operator,
        OptionValues = c.OptionValues.ToList(),
    };
}

public sealed class SurveyOptionBuilderViewModel
{
    public Guid? Id { get; set; }
    public string Value { get; set; } = string.Empty;
    public Dictionary<string, string> Label { get; set; } = new(StringComparer.Ordinal);

    public OptionInput ToInput(int order) => new(Id, order, Value, new LocalizedText(Label));

    public static SurveyOptionBuilderViewModel FromInput(OptionInput o) => new()
    {
        Id = o.Id,
        Value = o.Value,
        Label = SurveyBuilderViewModel.ToDict(o.Label),
    };
}
