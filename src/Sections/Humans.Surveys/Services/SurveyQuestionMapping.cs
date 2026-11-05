using Humans.Surveys.Contracts;
using Humans.Surveys.Domain;

namespace Humans.Surveys.Services;

internal static class SurveyQuestionMapping
{
    internal static IReadOnlyList<QuestionInput> ToQuestionInputs(Survey survey)
        => survey.Questions
            .OrderBy(question => question.PageNumber)
            .ThenBy(question => question.Order)
            .Select(question => new QuestionInput(
                question.Id,
                question.PageNumber,
                question.Order,
                question.Type,
                question.Prompt,
                question.HelpText,
                question.IsRequired,
                question.RatingMin,
                question.RatingMax,
                question.RatingMinLabel,
                question.RatingMaxLabel,
                question.ShowIf,
                question.Options
                    .OrderBy(option => option.Order)
                    .Select(option => new OptionInput(
                        option.Id,
                        option.Order,
                        option.Value,
                        option.Label))
                    .ToList(),
                question.GridSelectionMode,
                question.GridRows?
                    .Select(row => new GridRowInput(row.Value, row.Label))
                    .ToList(),
                question.InformationImages?
                    .Select(image => new InformationImageInput(
                        image.Id,
                        image.Label,
                        image.AltText,
                        image.StoragePath,
                        image.ContentType,
                        image.FileName))
                    .ToList(),
                question.RankedSettings,
                question.RankedUnavailableOptionValues))
            .ToList();

    /// <summary>Maps builder input to tracked entities, assigning new ids where the input id is null.</summary>
    internal static List<SurveyQuestion> MapQuestions(Guid surveyId, SurveyEditInput input)
        => input.Questions.Select(q =>
        {
            var questionId = q.Id ?? Guid.NewGuid();
            return new SurveyQuestion
            {
                Id = questionId,
                SurveyId = surveyId,
                PageNumber = q.PageNumber,
                Order = q.Order,
                Type = q.Type,
                Prompt = q.Prompt,
                HelpText = q.HelpText,
                IsRequired = q.IsRequired,
                RatingMin = q.RatingMin,
                RatingMax = q.RatingMax,
                RatingMinLabel = q.RatingMinLabel,
                RatingMaxLabel = q.RatingMaxLabel,
                GridSelectionMode = q.Type == SurveyQuestionType.Grid ? q.GridSelectionMode : null,
                GridRows = q.Type == SurveyQuestionType.Grid
                    ? (q.GridRows ?? []).Select(row => new SurveyGridRow(row.Value, row.Label)).ToList()
                    : null,
                InformationImages = q.Type == SurveyQuestionType.Information
                    ? (q.InformationImages ?? []).Select(image => new SurveyInformationImage(
                        image.Id!.Value,
                        image.StoragePath!,
                        image.ContentType!,
                        image.FileName!,
                        image.Label,
                        image.AltText)).ToList()
                    : null,
                RankedSettings = q.Type == SurveyQuestionType.RankedChoice
                    ? q.RankedSettings ?? RankedQuestionSettings.Default
                    : null,
                RankedUnavailableOptionValues = q.Type == SurveyQuestionType.RankedChoice
                    ? (q.RankedUnavailableOptionValues ?? []).Distinct(StringComparer.Ordinal).ToList()
                    : null,
                ShowIf = q.ShowIf,
                Options = q.Options.Select(o => new SurveyQuestionOption
                {
                    Id = o.Id ?? Guid.NewGuid(),
                    QuestionId = questionId,
                    Order = o.Order,
                    Value = o.Value,
                    Label = o.Label,
                }).ToList(),
            };
        }).ToList();
}
