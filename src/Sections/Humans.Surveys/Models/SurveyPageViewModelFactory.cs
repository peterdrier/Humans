using Humans.Base.Extensions;
using Humans.Surveys.Services;

namespace Humans.Surveys.Models;

/// <summary>
/// Shared projection from an authored survey plus wizard state to the respondent page model.
/// Both live answering and read-only admin preview use this so question rendering cannot drift.
/// </summary>
internal static class SurveyPageViewModelFactory
{
    public static string ResolveCulture(string? requested, string surveyDefault)
    {
        if (requested.IsSupportedCultureCode()) return requested!;
        if (surveyDefault.IsSupportedCultureCode()) return surveyDefault;
        return CultureCatalog.DefaultCultureCode;
    }

    public static SurveyPageViewModel Build(
        SurveyWizardState state,
        SurveyEditInput editable,
        IReadOnlyList<QuestionInput> questions,
        IReadOnlyList<int> pages,
        bool isPublic,
        string routeKey,
        bool isPreview = false,
        Guid? previewSurveyId = null)
    {
        var step = pages.ToList().IndexOf(state.CurrentPage);

        return new SurveyPageViewModel
        {
            Token = isPublic || isPreview ? string.Empty : routeKey,
            IsPublic = isPublic,
            Slug = isPublic ? routeKey : string.Empty,
            IsPreview = isPreview,
            PreviewSurveyId = previewSurveyId,
            PreviewCulture = state.Culture,
            PreviousPreviewPage = isPreview && step > 0 ? pages[step - 1] : null,
            NextPreviewPage = isPreview && step >= 0 && step < pages.Count - 1 ? pages[step + 1] : null,
            Page = state.CurrentPage,
            Title = editable.Title.Resolve(state.Culture, editable.DefaultCulture),
            StepNumber = step < 0 ? 1 : step + 1,
            TotalSteps = pages.Count,
            CanGoBack = step > 0,
            IsLastStep = step >= 0 && step == pages.Count - 1,
            Questions = questions.Select(q => BuildQuestion(q, state, editable)).ToList(),
        };
    }

    private static SurveyPageQuestion BuildQuestion(
        QuestionInput question,
        SurveyWizardState state,
        SurveyEditInput editable)
    {
        state.Answers.TryGetValue(question.Id!.Value.ToString(), out var prior);
        return new SurveyPageQuestion
        {
            Id = question.Id.Value,
            Type = question.Type,
            Prompt = question.Prompt.Resolve(state.Culture, editable.DefaultCulture),
            HelpText = question.HelpText.Resolve(state.Culture, editable.DefaultCulture),
            IsRequired = question.IsRequired,
            RatingMin = question.RatingMin,
            RatingMax = question.RatingMax,
            RatingMinLabel = question.RatingMinLabel.Resolve(state.Culture, editable.DefaultCulture),
            RatingMaxLabel = question.RatingMaxLabel.Resolve(state.Culture, editable.DefaultCulture),
            Options = question.Options
                .Select(option => new SurveyPageOption(
                    option.Value,
                    option.Label.Resolve(state.Culture, editable.DefaultCulture)))
                .ToList(),
            GridSelectionMode = question.GridSelectionMode,
            GridRows = (question.GridRows ?? [])
                .Select(row => new SurveyPageGridRow(
                    row.Value,
                    row.Label.Resolve(state.Culture, editable.DefaultCulture)))
                .ToList(),
            SelectedOptionValues = prior?.SelectedOptionValues ?? [],
            GridSelections = prior?.GridSelections.ToDictionary(
                kv => kv.Key,
                kv => (IReadOnlyList<string>)kv.Value,
                StringComparer.Ordinal)
                ?? new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal),
            TextValue = prior?.TextValue,
            RatingValue = prior?.RatingValue,
        };
    }
}
