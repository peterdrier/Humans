using Humans.Surveys.Contracts;
using Humans.Base.Attributes;
using Humans.Surveys.Domain;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;

namespace Humans.Surveys.Models;

/// <summary>
/// Fully resolved to the chosen culture: the view is dumb. Pages are numbered for the visible subset ("Page 2 of
/// 3"), not by the survey's raw page numbers.
/// </summary>
internal sealed class SurveyPageViewModel
{
    public string Token { get; init; } = string.Empty;

    /// <summary>True on the public-slug path: the form posts to <c>Public/Page</c> with <see cref="Slug"/> instead of <c>Answer/Page</c> with the token.</summary>
    public bool IsPublic { get; init; }

    /// <summary>The public slug (only set when <see cref="IsPublic"/>); drives the post route on the public path.</summary>
    public string Slug { get; init; } = string.Empty;

    public bool IsPreview { get; init; }

    public Guid? PreviewSurveyId { get; init; }
    public string PreviewCulture { get; init; } = string.Empty;
    public int? PreviousPreviewPage { get; init; }
    public int? NextPreviewPage { get; init; }

    /// <summary>The survey's raw page number, posted back so the server re-validates the right page.</summary>
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

internal sealed record SurveyPageOption(string Value, string Label);

internal sealed record SurveyPageGridRow(string Value, string Label);

internal sealed record SurveyPageInformationImage(Guid Id, string Url, string Label, string AltText);

internal sealed record SurveyPreviewNoticeModel(
    Guid SurveyId,
    string Message,
    string AdditionalCssClasses = "");

internal sealed class SurveyPageInputModel
{
    // Non-nullable strings are implicitly [Required]; the public path posts no Token, and blank
    // grid/ranked values are filtered by the controller, so none of them may fail validation.
    [ValidateNever]
    public string Token { get; set; } = string.Empty;
    public int Page { get; set; }
    public List<SurveyPostedAnswer> Answers { get; set; } = [];

    public bool Back { get; set; }
}

internal sealed class SurveyPostedAnswer
{
    public Guid QuestionId { get; set; }
    public List<string> SelectedOptionValues { get; set; } = [];
    public List<SurveyPostedGridRow> GridRows { get; set; } = [];
    public string? TextValue { get; set; }
    public int? RatingValue { get; set; }
    public List<SurveyPostedRankedOption> RankedOptions { get; set; } = [];
}

internal sealed class SurveyPostedGridRow
{
    [ValidateNever]
    public string RowValue { get; set; } = string.Empty;
    public List<string> SelectedColumnValues { get; set; } = [];
}

internal sealed class SurveyPostedRankedOption
{
    [ValidateNever]
    public string OptionValue { get; set; } = string.Empty;
    public string? Selection { get; set; }
}

internal sealed class SurveyThankYouViewModel
{
    public string Title { get; init; } = string.Empty;
    public string ThankYou { get; init; } = string.Empty;
    public bool IsPreview { get; init; }
    public Guid? PreviewSurveyId { get; init; }
}
