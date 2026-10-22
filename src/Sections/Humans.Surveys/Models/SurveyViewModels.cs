using Humans.Surveys.Contracts;
using Humans.Base.Attributes;
using Humans.Base.Extensions;
using Humans.Surveys.Services;
using Humans.Surveys.Domain;
using NodaTime;
using NodaTime.Text;

namespace Humans.Surveys.Models;

/// <summary>Sorted in the controller.</summary>
internal sealed class SurveyAdminIndexViewModel
{
    public IReadOnlyList<SurveyAdminSummary> Surveys { get; init; } = [];
    public bool IsBoardOrAdmin { get; init; }
}

internal sealed class SurveyPendingApprovalViewModel
{
    public IReadOnlyList<SurveyPendingApprovalItem> Items { get; init; } = [];
}

/// <summary>Side-effect-free.</summary>
internal sealed class SurveyEmailPreviewViewModel
{
    public Guid SurveyId { get; init; }
    public string Recipient { get; init; } = string.Empty;
    public string Subject { get; init; } = string.Empty;
    public string HtmlBody { get; init; } = string.Empty;
}

internal sealed record SurveyTeamOption(Guid Id, string Name);

/// <summary>The wizard intro/privacy step. Carries the token through to the Start POST.</summary>
internal sealed class SurveyIntroViewModel
{
    public string Token { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    [MarkdownContent]
    public string Intro { get; init; } = string.Empty;

    /// <summary>Posted back so the wizard runs in it.</summary>
    public string Culture { get; init; } = CultureCatalog.DefaultCultureCode;

    public bool AllowAnonymous { get; init; }

    /// <summary>
    /// On the invited path when anonymity is allowed, and on the public path for a logged-in Human. Logged-out public
    /// visitors remain Anonymous.
    /// </summary>
    public bool ShowAnonymitySelector { get; init; }

    /// <summary>The intro form posts to <c>Public/Start</c> with <see cref="Slug"/> instead of <c>Answer/Start</c> with the token.</summary>
    public bool IsPublic { get; init; }

    public string Slug { get; init; } = string.Empty;

    /// <summary>Identified resume.</summary>
    public bool HasResumableDraft { get; init; }

    public bool IsAsociadoVote { get; init; }

    public bool IsPreview { get; init; }

    /// <summary>Set only when <see cref="IsPreview"/>.</summary>
    public Guid? PreviewSurveyId { get; init; }

    public IReadOnlyList<string> Cultures { get; } = CultureCatalog.SupportedCultureCodes;
}

internal sealed class SurveyStartViewModel
{
    public string Token { get; set; } = string.Empty;
    public ResponseAnonymity Anonymity { get; set; } = ResponseAnonymity.Identified;
    public string Culture { get; set; } = CultureCatalog.DefaultCultureCode;
}

/// <summary><see cref="Reason"/> is a friendly explanation key.</summary>
internal sealed class SurveyClosedViewModel
{
    public string? Reason { get; init; }
}

/// <summary>Invite rows are sorted in the controller.</summary>
internal sealed class SurveySendViewModel
{
    public Guid Id { get; init; }
    public string Title { get; init; } = string.Empty;
    public SurveyStatus Status { get; init; }
    public SurveyAudienceType? AudienceType { get; init; }
    public string? AudienceTeamName { get; init; }
    public LocalDate? AudienceLoggedInSince { get; init; }
    public int NewRecipientCount { get; init; }
    public IReadOnlyList<SurveyInviteStatus> Invitations { get; init; } = [];
    public bool IsAsociadoVote { get; init; }

    public bool CanSend => Status == SurveyStatus.Open && AudienceType is not null && NewRecipientCount > 0;
}

/// <summary>
/// Renders one input per supported culture for a localized field. <paramref name="NamePrefix"/> is the
/// bound name without the culture key (e.g. <c>Title</c> or <c>Questions[k].Prompt</c>); the partial
/// emits <c>NamePrefix[culture]</c>.
/// </summary>
internal sealed record SurveyLocalizedFieldModel(
    string NamePrefix,
    string Label,
    IReadOnlyDictionary<string, string> Values,
    bool Multiline = false,
    int? MaxLength = null,
    bool Markdown = false);

/// <summary>One question card in the builder. <paramref name="Key"/> is the non-sequential indexer key (or the <c>__QKEY__</c> placeholder in the JS template).</summary>
internal sealed record SurveyQuestionCardModel(
    string Key,
    SurveyQuestionBuilderViewModel Question,
    bool RankedDefinitionLocked = false);

/// <summary>One option row in the builder. Keys are non-sequential indexers (or <c>__QKEY__</c>/<c>__OKEY__</c> placeholders in templates).</summary>
internal sealed record SurveyOptionRowModel(string QuestionKey, string OptionKey, SurveyOptionBuilderViewModel Option);

internal sealed record SurveyGridRowModel(string QuestionKey, string RowKey, SurveyGridRowBuilderViewModel Row);

internal sealed record SurveyInformationImageRowModel(
    string QuestionKey,
    string ImageKey,
    SurveyInformationImageBuilderViewModel Image);

/// <summary>One show-if clause row in the builder. Keys are non-sequential indexers (or <c>__QKEY__</c>/<c>__CKEY__</c> placeholders in templates).</summary>
internal sealed record SurveyBranchClauseRowModel(string QuestionKey, string ClauseKey, SurveyBranchClauseBuilderViewModel Clause);

/// <summary>
/// Questions, options and show-if clauses use non-sequential indexers (a <c>.Index</c> hidden field per collection) so
/// rows can be added or removed client-side without renumbering. New questions get their
/// <see cref="SurveyQuestionBuilderViewModel.Id"/> client-side so clauses can reference unsaved questions (the service
/// honours supplied ids).
/// </summary>
internal sealed class SurveyBuilderViewModel
{
    public Guid? Id { get; set; }
    public SurveyStatus Status { get; set; } = SurveyStatus.Draft;
    public string? RejectionNote { get; set; }

    public Dictionary<string, string> Title { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Intro { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> ThankYou { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> InvitationEmailSubject { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> InvitationEmailMessage { get; set; } = new(StringComparer.Ordinal);

    public string DefaultCulture { get; set; } = CultureCatalog.DefaultCultureCode;
    public bool AllowAnonymous { get; set; }
    public bool IsAsociadoVote { get; set; }
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
    public bool HasSavedAnswers { get; set; }

    /// <summary>
    /// Opening, closing, previewing and reviewing recipients are <c>BoardOrAdmin</c> actions, though any approved human
    /// may author: showing an author a button that ends in 403 is a bug.
    /// </summary>
    public bool IsBoardOrAdmin { get; set; }

    /// <summary>Computed from the stored survey and its Submit authorization on every render.</summary>
    [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever]
    public bool CanSubmit { get; set; }

    public bool IsNew => Id is null;
    public bool IsDefinitionLocked => !IsNew && IsAsociadoVote && Status != SurveyStatus.Draft;

    // datetime-local <input> values (empty when unset).
    public string OpensAtInput => FormatLocal(OpensAt);
    public string ClosesAtInput => FormatLocal(ClosesAt);

    // date <input> value (empty when unset).
    public string AudienceLoggedInSinceInput
        => AudienceLoggedInSince is null ? string.Empty : LocalDatePattern.Iso.Format(AudienceLoggedInSince.Value);

    private static string FormatLocal(LocalDateTime? local)
        => local is null ? string.Empty : DateFormattingExtensions.PlacementDateTimePattern.Format(local.Value);

    /// <summary>Question and option order is the posted order.</summary>
    public SurveyEditInput ToEditInput(DateTimeZone zone) => new(
        new LocalizedText(Title),
        new LocalizedText(Intro),
        new LocalizedText(ThankYou),
        new LocalizedText(InvitationEmailSubject),
        new LocalizedText(InvitationEmailMessage),
        string.IsNullOrWhiteSpace(DefaultCulture) ? CultureCatalog.DefaultCultureCode : DefaultCulture,
        AllowAnonymous,
        ToInstant(OpensAt, zone),
        ToInstant(ClosesAt, zone),
        AudienceType,
        AudienceType == SurveyAudienceType.Team ? AudienceTeamId : null,
        AudienceType == SurveyAudienceType.LoggedInSince ? ToStartOfDayInstant(AudienceLoggedInSince, zone) : null,
        string.IsNullOrWhiteSpace(PublicSlug) ? null : PublicSlug,
        ToQuestionInputsInPageOrder(),
        IsAsociadoVote);

    private IReadOnlyList<QuestionInput> ToQuestionInputsInPageOrder()
    {
        var nextOrderByPage = new Dictionary<int, int>();
        return Questions.Select(question =>
        {
            var page = question.PageNumber <= 0 ? 1 : question.PageNumber;
            var order = nextOrderByPage.GetValueOrDefault(page);
            nextOrderByPage[page] = order + 1;
            return question.ToInput(order);
        }).ToList();
    }

    public static SurveyBuilderViewModel FromDetail(SurveyDetail detail, IReadOnlyList<SurveyTeamOption> teams, DateTimeZone zone)
    {
        var e = detail.Editable;
        return new SurveyBuilderViewModel
        {
            Id = detail.Id,
            Status = detail.Status,
            RejectionNote = detail.RejectionNote,
            Title = ToDict(e.Title),
            Intro = ToDict(e.Intro),
            ThankYou = ToDict(e.ThankYou),
            InvitationEmailSubject = ToDict(e.InvitationEmailSubject),
            InvitationEmailMessage = ToDict(e.InvitationEmailMessage),
            DefaultCulture = e.DefaultCulture,
            AllowAnonymous = e.AllowAnonymous,
            IsAsociadoVote = e.IsAsociadoVote,
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

internal sealed class SurveyQuestionBuilderViewModel
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
    public GridSelectionMode? GridSelectionMode { get; set; }
    public bool RankedAllowEqualRanks { get; set; } = true;
    public bool RankedAllowReject { get; set; }
    public BranchCombine ShowIfCombine { get; set; } = BranchCombine.All;
    public List<SurveyBranchClauseBuilderViewModel> ShowIfClauses { get; set; } = [];
    public List<SurveyOptionBuilderViewModel> Options { get; set; } = [];
    public List<SurveyGridRowBuilderViewModel> GridRows { get; set; } = [];
    public List<SurveyInformationImageBuilderViewModel> InformationImages { get; set; } = [];

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
        Options.Select((o, i) => o.ToInput(i)).ToList(),
        Type == SurveyQuestionType.Grid ? GridSelectionMode : null,
        Type == SurveyQuestionType.Grid ? GridRows.Select(r => r.ToInput()).ToList() : null,
        Type == SurveyQuestionType.Information
            ? InformationImages.Select(image => image.ToInput()).ToList()
            : null,
        Type == SurveyQuestionType.RankedChoice
            ? new RankedQuestionSettings(
                RankedAllowEqualRanks,
                RankedAllowReject,
                RankedVotingMethod.RankedPairs)
            : null);

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
        GridSelectionMode = q.GridSelectionMode ?? Humans.Surveys.Contracts.GridSelectionMode.Single,
        GridRows = q.GridRows?.Select(SurveyGridRowBuilderViewModel.FromInput).ToList() ?? [],
        InformationImages = q.InformationImages?
            .Select(SurveyInformationImageBuilderViewModel.FromInput)
            .ToList() ?? [],
        RankedAllowEqualRanks = q.RankedSettings?.AllowEqualRanks ?? true,
        RankedAllowReject = q.RankedSettings?.AllowReject ?? false,
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

internal sealed class SurveyInformationImageBuilderViewModel
{
    public Guid? Id { get; set; }
    public Dictionary<string, string> Label { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> AltText { get; set; } = new(StringComparer.Ordinal);
    public string? ExistingStoragePath { get; set; }
    public string? ExistingFileName { get; set; }
    public IFormFile? Upload { get; set; }

    public InformationImageInput ToInput() => new(
        Id,
        new LocalizedText(Label),
        new LocalizedText(AltText),
        ExistingStoragePath,
        FileName: ExistingFileName,
        Upload: Upload is { Length: > 0 }
            ? new SurveyImageUpload(Upload.OpenReadStream(), Upload.ContentType, Upload.FileName, Upload.Length)
            : null);

    public static SurveyInformationImageBuilderViewModel FromInput(InformationImageInput image) => new()
    {
        Id = image.Id,
        Label = SurveyBuilderViewModel.ToDict(image.Label),
        AltText = SurveyBuilderViewModel.ToDict(image.AltText),
        ExistingStoragePath = image.StoragePath,
        ExistingFileName = image.FileName,
    };
}

internal sealed class SurveyBranchClauseBuilderViewModel
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

internal sealed class SurveyOptionBuilderViewModel
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

internal sealed class SurveyGridRowBuilderViewModel
{
    public string Value { get; set; } = string.Empty;
    public Dictionary<string, string> Label { get; set; } = new(StringComparer.Ordinal);

    public GridRowInput ToInput() => new(Value, new LocalizedText(Label));

    public static SurveyGridRowBuilderViewModel FromInput(GridRowInput row) => new()
    {
        Value = row.Value,
        Label = SurveyBuilderViewModel.ToDict(row.Label),
    };
}
