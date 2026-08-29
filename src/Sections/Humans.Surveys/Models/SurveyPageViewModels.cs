using Humans.Surveys.Contracts;
using Humans.Base.Attributes;
using Humans.Surveys.Domain;

namespace Humans.Surveys.Models;

// ── Answering wizard (question pages) ───────────────────────────────────────

/// <summary>
/// One page of the answering wizard, fully resolved to the chosen culture (the view is dumb — the
/// controller does all <c>LocalizedText</c> resolution). Pages are numbered for the visible subset
/// (e.g. "Page 2 of 3"), not by the survey's raw page numbers.
/// </summary>
internal sealed class SurveyPageViewModel
{
    public string Token { get; init; } = string.Empty;

    /// <summary>True on the public-slug path: the form posts to <c>Public/Page</c> with <see cref="Slug"/> instead of <c>Answer/Page</c> with the token.</summary>
    public bool IsPublic { get; init; }

    /// <summary>The public slug (only set when <see cref="IsPublic"/>); drives the post route on the public path.</summary>
    public string Slug { get; init; } = string.Empty;

    /// <summary>True for the protected, read-only admin preview flow.</summary>
    public bool IsPreview { get; init; }

    public Guid? PreviewSurveyId { get; init; }
    public string PreviewCulture { get; init; } = string.Empty;
    public int? PreviousPreviewPage { get; init; }
    public int? NextPreviewPage { get; init; }

    /// <summary>The survey's raw page number this view renders (posted back so the server re-validates the right page).</summary>
    public int Page { get; init; }

    public string Title { get; init; } = string.Empty;
    public bool IsAsociadoVote { get; init; }

    /// <summary>1-based position of this page among the visible pages.</summary>
    public int StepNumber { get; init; }

    /// <summary>Count of visible pages given the answers so far.</summary>
    public int TotalSteps { get; init; }

    /// <summary>True when an earlier visible page exists to navigate back to.</summary>
    public bool CanGoBack { get; init; }

    /// <summary>True when this is the last visible page (the Next button submits).</summary>
    public bool IsLastStep { get; init; }

    public IReadOnlyList<SurveyPageQuestion> Questions { get; init; } = [];
}

/// <summary>One resolved question on a wizard page, with its prior answer pre-filled for re-render.</summary>
internal sealed class SurveyPageQuestion
{
    public Guid Id { get; init; }
    public SurveyQuestionType Type { get; init; }
    public string Prompt { get; init; } = string.Empty;
    [MarkdownContent]
    public string HelpText { get; init; } = string.Empty;
    public bool IsRequired { get; init; }
    public int? RatingMin { get; init; }
    public int? RatingMax { get; init; }
    public string RatingMinLabel { get; init; } = string.Empty;
    public string RatingMaxLabel { get; init; } = string.Empty;
    public IReadOnlyList<SurveyPageOption> Options { get; init; } = [];
    public GridSelectionMode? GridSelectionMode { get; init; }
    public IReadOnlyList<SurveyPageGridRow> GridRows { get; init; } = [];
    public IReadOnlyList<SurveyPageInformationImage> InformationImages { get; init; } = [];

    // Prior answer (for re-render after a validation failure or a Back navigation).
    public IReadOnlyList<string> SelectedOptionValues { get; init; } = [];
    public IReadOnlyDictionary<string, IReadOnlyList<string>> GridSelections { get; init; }
        = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
    public string? TextValue { get; init; }
    public int? RatingValue { get; init; }
    public RankedAnswer? RankedValue { get; init; }
    public bool RankedAllowEqualRanks { get; init; }
    public bool RankedAllowReject { get; init; }
}

/// <summary>One resolved choice option: stable machine <see cref="Value"/> + display <see cref="Label"/>.</summary>
internal sealed record SurveyPageOption(string Value, string Label);

/// <summary>One resolved Grid row.</summary>
internal sealed record SurveyPageGridRow(string Value, string Label);

/// <summary>One resolved, publicly served image in an Information survey item.</summary>
internal sealed record SurveyPageInformationImage(Guid Id, string Url, string Label, string AltText);

/// <summary>Pure presentation model for the shared author-preview notice.</summary>
internal sealed record SurveyPreviewNoticeModel(
    Guid SurveyId,
    string Message,
    string AdditionalCssClasses = "");

/// <summary>Posted by one wizard page. <see cref="Answers"/> binds via indexed form fields.</summary>
internal sealed class SurveyPageInputModel
{
    public string Token { get; set; } = string.Empty;
    public int Page { get; set; }
    public List<SurveyPostedAnswer> Answers { get; set; } = [];

    /// <summary>True when the user pressed Back rather than Next/Submit.</summary>
    public bool Back { get; set; }
}

/// <summary>One posted answer for a question on the current page.</summary>
internal sealed class SurveyPostedAnswer
{
    public Guid QuestionId { get; set; }
    public List<string> SelectedOptionValues { get; set; } = [];
    public List<SurveyPostedGridRow> GridRows { get; set; } = [];
    public string? TextValue { get; set; }
    public int? RatingValue { get; set; }
    public List<SurveyPostedRankedOption> RankedOptions { get; set; } = [];
}

/// <summary>One posted Grid row and its selected column values.</summary>
internal sealed class SurveyPostedGridRow
{
    public string RowValue { get; set; } = string.Empty;
    public List<string> SelectedColumnValues { get; set; } = [];
}

internal sealed class SurveyPostedRankedOption
{
    public string OptionValue { get; set; } = string.Empty;
    public string? Selection { get; set; }
}

/// <summary>The closing thank-you page, with the survey's ThankYou copy resolved for display.</summary>
internal sealed class SurveyThankYouViewModel
{
    public string Title { get; init; } = string.Empty;
    public string ThankYou { get; init; } = string.Empty;
    public bool IsPreview { get; init; }
    public Guid? PreviewSurveyId { get; init; }
}
