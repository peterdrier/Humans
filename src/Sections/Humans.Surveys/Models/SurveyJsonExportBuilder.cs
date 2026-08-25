using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Humans.Base.Extensions;
using Humans.Surveys.Contracts;
using Humans.Surveys.Domain;

namespace Humans.Surveys.Models;

/// <summary>
/// Builds the compact JSON download: question metadata appears once in <c>Questions</c>, while
/// response answers carry only stable values that join back to that schema.
/// </summary>
internal static class SurveyJsonExportBuilder
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static byte[] Build(SurveyResponseExport export)
    {
        ArgumentNullException.ThrowIfNull(export);

        var compact = new SurveyJsonExport(
            export.SurveyId,
            export.Title,
            export.DefaultCulture,
            export.Questions,
            export.Rows.Select(row => new SurveyJsonRow(
                row.ResponseId,
                row.Anonymity,
                row.InputMethod,
                row.Culture,
                row.SubmittedAt.ToIso8601(),
                row.UserId,
                row.UserName,
                row.Answers.Select(answer => new SurveyJsonAnswer(
                    answer.QuestionId,
                    answer.SelectedValues.Count > 0 ? answer.SelectedValues : null,
                    answer.TextValue,
                    answer.RatingValue,
                    answer.GridSelections is { Count: > 0 } ? answer.GridSelections : null))
                    .ToList()))
                .ToList());

        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(compact, Options));
    }

    private sealed record SurveyJsonExport(
        Guid SurveyId,
        string Title,
        string DefaultCulture,
        IReadOnlyList<SurveyExportQuestion> Questions,
        IReadOnlyList<SurveyJsonRow> Rows);

    private sealed record SurveyJsonRow(
        Guid ResponseId,
        ResponseAnonymity Anonymity,
        SurveyInputMethod InputMethod,
        string Culture,
        string? SubmittedAt,
        Guid? UserId,
        string? UserName,
        IReadOnlyList<SurveyJsonAnswer> Answers);

    private sealed record SurveyJsonAnswer(
        Guid QuestionId,
        IReadOnlyList<string>? SelectedValues,
        string? TextValue,
        int? RatingValue,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? GridSelections);
}
