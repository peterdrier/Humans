using Humans.Domain.Enums;

namespace Humans.Web.Models.Survey;

// ── Answering wizard (question pages) ───────────────────────────────────────

/// <summary>
/// One page of the answering wizard, fully resolved to the chosen culture (the view is dumb — the
/// controller does all <c>LocalizedText</c> resolution). Pages are numbered for the visible subset
/// (e.g. "Page 2 of 3"), not by the survey's raw page numbers.
/// </summary>
public sealed class SurveyPageViewModel
{
    public string Token { get; init; } = string.Empty;

    /// <summary>True on the public-slug path: the form posts to <c>Public/Page</c> with <see cref="Slug"/> instead of <c>Answer/Page</c> with the token.</summary>
    public bool IsPublic { get; init; }

    /// <summary>The public slug (only set when <see cref="IsPublic"/>); drives the post route on the public path.</summary>
    public string Slug { get; init; } = string.Empty;

    /// <summary>The survey's raw page number this view renders (posted back so the server re-validates the right page).</summary>
    public int Page { get; init; }

    public string Title { get; init; } = string.Empty;

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
public sealed class SurveyPageQuestion
{
    public Guid Id { get; init; }
    public SurveyQuestionType Type { get; init; }
    public string Prompt { get; init; } = string.Empty;
    public string HelpText { get; init; } = string.Empty;
    public bool IsRequired { get; init; }
    public int? RatingMin { get; init; }
    public int? RatingMax { get; init; }
    public string RatingMinLabel { get; init; } = string.Empty;
    public string RatingMaxLabel { get; init; } = string.Empty;
    public IReadOnlyList<SurveyPageOption> Options { get; init; } = [];

    // Prior answer (for re-render after a validation failure or a Back navigation).
    public IReadOnlyList<string> SelectedOptionValues { get; init; } = [];
    public string? TextValue { get; init; }
    public int? RatingValue { get; init; }
}

/// <summary>One resolved choice option: stable machine <see cref="Value"/> + display <see cref="Label"/>.</summary>
public sealed record SurveyPageOption(string Value, string Label);

/// <summary>Posted by one wizard page. <see cref="Answers"/> binds via indexed form fields.</summary>
public sealed class SurveyPageInputModel
{
    public string Token { get; set; } = string.Empty;
    public int Page { get; set; }
    public List<SurveyPostedAnswer> Answers { get; set; } = [];

    /// <summary>True when the user pressed Back rather than Next/Submit.</summary>
    public bool Back { get; set; }
}

/// <summary>One posted answer for a question on the current page.</summary>
public sealed class SurveyPostedAnswer
{
    public Guid QuestionId { get; set; }
    public List<string> SelectedOptionValues { get; set; } = [];
    public string? TextValue { get; set; }
    public int? RatingValue { get; set; }
}

/// <summary>The closing thank-you page, with the survey's ThankYou copy resolved for display.</summary>
public sealed class SurveyThankYouViewModel
{
    public string Title { get; init; } = string.Empty;
    public string ThankYou { get; init; } = string.Empty;
}
