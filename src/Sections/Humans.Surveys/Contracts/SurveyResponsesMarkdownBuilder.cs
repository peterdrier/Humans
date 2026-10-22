using System.Globalization;
using System.Text;
using System.Text.Json;
using NodaTime.Text;

namespace Humans.Surveys.Contracts;

/// <summary>
/// The token-lean table an agent reads. The CSV export's columns without the join keys; choice cells hold the stable
/// option <b>values</b> (<c>a|b</c>), not labels, so the table joins against the definition endpoint.
/// </summary>
public static class SurveyResponsesMarkdownBuilder
{
    private static readonly InstantPattern SubmittedPattern = InstantPattern.General; // ISO-8601 UTC, invariant.

    public static string Build(IReadOnlyList<SurveyExportQuestion> questions, IReadOnlyList<SurveyExportRow> rows)
    {
        ArgumentNullException.ThrowIfNull(questions);
        ArgumentNullException.ThrowIfNull(rows);

        var headers = new List<string> { "anonymity", "input_method", "submitted_at", "user_name" };
        headers.AddRange(questions.Select(q => Escape(q.Prompt)));

        var sb = new StringBuilder();
        sb.Append("| ").Append(string.Join(" | ", headers)).Append(" |\n");
        sb.Append('|').Append(string.Join("|", headers.Select(_ => " --- "))).Append("|\n");

        foreach (var row in rows)
        {
            var byQuestion = row.Answers.ToDictionary(a => a.QuestionId);

            var cells = new List<string>
            {
                row.Anonymity.ToString(),
                row.InputMethod.ToString(),
                row.SubmittedAt is { } at ? SubmittedPattern.Format(at) : string.Empty,
                Escape(row.UserName ?? string.Empty),   // blank for non-Identified rows
            };

            cells.AddRange(questions.Select(q =>
                byQuestion.TryGetValue(q.QuestionId, out var answer) ? Escape(CellValue(q, answer)) : string.Empty));

            sb.Append("| ").Append(string.Join(" | ", cells)).Append(" |\n");
        }

        return sb.ToString();
    }

    /// <summary>Choice values flattened <c>a|b</c>, free text verbatim, the rating integer, or JSON for Grid selections and ranked ballots.</summary>
    internal static string CellValue(SurveyExportQuestion question, SurveyExportAnswer answer) => question.Type switch
    {
        SurveyQuestionType.SingleChoice or SurveyQuestionType.MultiChoice =>
            string.Join("|", answer.SelectedValues),
        SurveyQuestionType.Grid =>
            JsonSerializer.Serialize(
                (answer.GridSelections ?? new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal))
                .ToDictionary(
                    selection => selection.Key,
                    selection => selection.Value,
                    StringComparer.Ordinal)),
        SurveyQuestionType.RankedChoice =>
            answer.RankedBallot is null ? string.Empty : JsonSerializer.Serialize(answer.RankedBallot),
        SurveyQuestionType.Rating =>
            answer.RatingValue?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
        _ => answer.TextValue ?? string.Empty,
    };

    /// <summary>Collapses newlines to a space and escapes the column separator.</summary>
    private static string Escape(string value) => value
        .Replace("\r\n", " ", StringComparison.Ordinal)
        .Replace('\r', ' ')
        .Replace('\n', ' ')
        .Replace("|", "\\|", StringComparison.Ordinal);
}
