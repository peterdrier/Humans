using Humans.Surveys.Contracts;
using Humans.Surveys.Domain;

namespace Humans.Surveys.Services;

internal static class SurveyDefinitionValidation
{
    internal static SurveyAuthoringError? Validate(IReadOnlyList<SurveyQuestion> questions, SurveyEditInput input) =>
        ValidateQuestionConfiguration(questions)
        ?? ValidateBranching(questions)
        ?? ValidateVoteConfiguration(input);

    private static SurveyAuthoringError? ValidateQuestionConfiguration(IReadOnlyList<SurveyQuestion> questions)
    {
        foreach (var question in questions)
        {
            if (question.Options.Any(option => option.Value.Length > 100))
                return new("Surveys_Validation_OptionValueLength",
                    $"Option values for question {question.Id} must be 100 characters or fewer.", 100);

            var error = question.Type switch
            {
                SurveyQuestionType.SingleChoice or SurveyQuestionType.MultiChoice => ValidateChoiceQuestion(question),
                SurveyQuestionType.Rating when (question.RatingMin ?? 1) > (question.RatingMax ?? 5) =>
                    new("Surveys_Validation_RatingBounds",
                        $"Rating question {question.Id} minimum must not exceed its maximum."),
                SurveyQuestionType.Information => ValidateInformationQuestion(question),
                SurveyQuestionType.RankedChoice => ValidateRankedQuestion(question),
                SurveyQuestionType.Grid => ValidateGridQuestion(question),
                _ => null,
            };
            if (error is not null) return error;
        }
        return null;

        static SurveyAuthoringError? ValidateChoiceQuestion(SurveyQuestion question)
        {
            if (question.Options.Count == 0)
                return new("Surveys_Validation_ChoiceOptionEmpty",
                    $"Choice question {question.Id} must have at least one option.", question.Id);

            return ValidateStableValues(question.Options.Select(option => option.Value),
                $"Choice question {question.Id} option", question.Id, "Surveys_Validation_ChoiceOption");
        }

        static SurveyAuthoringError? ValidateInformationQuestion(SurveyQuestion question)
        {
            if (question.IsRequired)
                return new("Surveys_Validation_InformationRequired", $"Information item {question.Id} cannot be required.", question.Id);

            var images = question.InformationImages ?? [];
            if (images.Count > SurveyImageUploads.MaxInformationImages)
            {
                return new("Surveys_Validation_InformationImagesLimit",
                    $"Information item {question.Id} can have at most {SurveyImageUploads.MaxInformationImages} images.", question.Id, SurveyImageUploads.MaxInformationImages);
            }

            if (!question.HelpText.Values.Values.Any(value => !string.IsNullOrWhiteSpace(value))
                && images.Count == 0)
            {
                return new("Surveys_Validation_InformationContentRequired",
                    $"Information item {question.Id} must contain Markdown or at least one image.", question.Id);
            }

            if (images.Any(image =>
                !image.Label.Values.Values.Any(value => !string.IsNullOrWhiteSpace(value))))
            {
                return new("Surveys_Validation_InformationLabelRequired",
                    $"Every image in Information item {question.Id} must have a label.", question.Id);
            }

            if (images.Any(image =>
                !image.AltText.Values.Values.Any(value => !string.IsNullOrWhiteSpace(value))))
            {
                return new("Surveys_Validation_InformationAltTextRequired",
                    $"Every image in Information item {question.Id} must have alt text.", question.Id);
            }
            return null;
        }

        static SurveyAuthoringError? ValidateGridQuestion(SurveyQuestion question)
        {
            if (question.GridSelectionMode is null
                || !Enum.IsDefined(question.GridSelectionMode.Value))
            {
                return new("Surveys_Validation_GridMode", $"Grid question {question.Id} must choose a selection mode.", question.Id);
            }

            var rows = question.GridRows ?? [];
            if (rows.Count == 0)
                return new("Surveys_Validation_GridRows", $"Grid question {question.Id} must have at least one row.", question.Id);

            if (question.Options.Count == 0 || question.Options.Count > 5)
            {
                return new("Surveys_Validation_GridColumns",
                    $"Grid question {question.Id} must have between one and five columns.", question.Id);
            }

            return ValidateStableValues(rows.Select(row => row.Value), $"Grid question {question.Id} row",
                       question.Id, "Surveys_Validation_GridRow")
                   ?? ValidateStableValues(question.Options.Select(option => option.Value), $"Grid question {question.Id} column",
                       question.Id, "Surveys_Validation_GridColumn");
        }

        static SurveyAuthoringError? ValidateRankedQuestion(SurveyQuestion question)
        {
            if (question.Options.Count < 2)
                return new("Surveys_Validation_RankedOptions",
                    $"Ranked-choice question {question.Id} must have at least two options.", question.Id);
            var error = ValidateStableValues(
                question.Options.OrderBy(option => option.Order).Select(option => option.Value),
                $"Ranked-choice question {question.Id} option", question.Id, "Surveys_Validation_RankedOption");
            if (error is not null) return error;
            if (question.RankedSettings is null
                || !Enum.IsDefined(question.RankedSettings.OfficialMethod))
            {
                return new("Surveys_Validation_RankedSettings",
                    $"Ranked-choice question {question.Id} must have valid ranking settings.", question.Id);
            }
            return null;
        }

        static SurveyAuthoringError? ValidateStableValues(IEnumerable<string> values, string description, Guid questionId, string keyPrefix)
        {
            var materialized = values.ToList();
            if (materialized.Any(string.IsNullOrWhiteSpace))
            {
                return new(keyPrefix + "Blank", $"{description} values must not be blank.", questionId);
            }

            if (materialized.Distinct(StringComparer.Ordinal).Count() != materialized.Count)
            {
                return new(keyPrefix + "Unique", $"{description} values must be unique.", questionId);
            }
            return null;
        }
    }

    private static SurveyAuthoringError? ValidateBranching(IReadOnlyList<SurveyQuestion> questions)
    {
        var offenders = SurveyBranchingEvaluator.ValidateNoForwardReferences(questions);
        if (offenders.Count > 0)
        {
            return new("Surveys_Validation_BranchEarlier",
                $"A branching condition references a question that is not strictly earlier. Offending question ids: {string.Join(", ", offenders)}.", string.Join(", ", offenders));
        }

        var emptyClauses = SurveyBranchingEvaluator.ValidateClauseOptionValues(questions);
        if (emptyClauses.Count > 0)
        {
            return new("Surveys_Validation_BranchEmptyClause",
                $"A branching Is/IsNot clause has no option values (the condition would be vacuous). Offending question ids: {string.Join(", ", emptyClauses)}.", string.Join(", ", emptyClauses));
        }

        var types = questions.ToDictionary(question => question.Id, question => question.Type);
        var nonAnswerSources = questions
            .Where(question => question.ShowIf?.Clauses.Any(clause =>
                types.GetValueOrDefault(clause.QuestionId) is
                    SurveyQuestionType.Grid or SurveyQuestionType.Information or SurveyQuestionType.RankedChoice) == true)
            .Select(question => question.Id)
            .ToList();
        if (nonAnswerSources.Count > 0)
        {
            return new("Surveys_Validation_BranchUnsupportedSource",
                $"Grid, RankedChoice, and Information questions cannot be branching sources. Offending question ids: {string.Join(", ", nonAnswerSources)}.", string.Join(", ", nonAnswerSources));
        }
        return null;
    }

    private static SurveyAuthoringError? ValidateVoteConfiguration(SurveyEditInput input)
    {
        if (!input.IsAsociadoVote) return null;
        if (input.AudienceType != SurveyAudienceType.Asociados)
            return new("Surveys_Validation_VoteAudience", "Asociado votes must target the Asociados audience.");
        if (input.AllowAnonymous)
            return new("Surveys_Validation_VoteAnonymity", "Asociado votes use fixed completion-tracked representation.");
        return null;
    }

    internal static SurveyAuthoringError? ValidateRankedDefinitionFrozen(
        IEnumerable<SurveyQuestion> existing,
        IEnumerable<SurveyQuestion> updated)
    {
        var oldRanked = existing
            .Where(question => question.Type == SurveyQuestionType.RankedChoice)
            .OrderBy(question => question.PageNumber).ThenBy(question => question.Order)
            .ToList();
        var newRanked = updated
            .Where(question => question.Type == SurveyQuestionType.RankedChoice)
            .OrderBy(question => question.PageNumber).ThenBy(question => question.Order)
            .ToList();
        if (oldRanked.Count != newRanked.Count)
            return new("Surveys_Validation_FrozenRankedCount", "Ranked-choice questions cannot be added or removed after voting starts.");
        for (var index = 0; index < oldRanked.Count; index++)
        {
            var before = oldRanked[index];
            var after = newRanked[index];
            var beforeValues = before.Options.OrderBy(option => option.Order).Select(option => option.Value);
            var afterValues = after.Options.OrderBy(option => option.Order).Select(option => option.Value);
            if (before.Id != after.Id
                || !beforeValues.SequenceEqual(afterValues, StringComparer.Ordinal)
                || before.RankedSettings != after.RankedSettings)
            {
                return new("Surveys_Validation_FrozenRankedSettings",
                    "Ranked-choice candidates, order, and settings cannot change after the first saved answer.");
            }
        }
        return null;
    }
}
