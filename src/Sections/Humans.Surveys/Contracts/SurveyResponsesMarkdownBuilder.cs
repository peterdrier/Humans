using System.Globalization;
using System.Text;
using System.Text.Json;
using Humans.Base.Extensions;

namespace Humans.Surveys.Contracts;

/// <summary>
/// The token-lean table an agent reads. The CSV export's columns without the join keys; choice cells hold the stable
/// option <b>values</b> (<c>a|b</c>), not labels, so the table joins against the definition endpoint.
/// </summary>
public static class SurveyResponsesMarkdownBuilder
{
    public static string Build(IReadOnlyList<SurveyExportQuestion> questions, IReadOnlyList<SurveyExportRow> rows)
    {
        ArgumentNullException.ThrowIfNull(questions);
        ArgumentNullException.ThrowIfNull(rows);

        var headers = new List<string> { "anonymity", "input_method", "submitted_at", "user_name" };
        headers.AddRange(questions.SelectMany(q => ColumnHeaders(q).Select(Escape)));

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
                row.SubmittedAt.ToIso8601() ?? string.Empty,
                Escape(row.UserName ?? string.Empty),   // blank for non-Identified rows
            };

            cells.AddRange(questions.SelectMany(q =>
                Cells(q, byQuestion.GetValueOrDefault(q.QuestionId)).Select(Escape)));

            sb.Append("| ").Append(string.Join(" | ", cells)).Append(" |\n");
        }

        return sb.ToString();
    }

    /// <summary>The question's column header, plus a comment column when it asks for a comment.</summary>
    internal static IEnumerable<string> ColumnHeaders(SurveyExportQuestion question)
        => question.CommentPrompt is null ? [question.Prompt] : [question.Prompt, $"{question.Prompt} (comment)"];

    /// <summary>The cells matching <see cref="ColumnHeaders"/>; blank when there is no answer.</summary>
    internal static IEnumerable<string> Cells(SurveyExportQuestion question, SurveyExportAnswer? answer)
    {
        yield return answer is null ? string.Empty : CellValue(question, answer);
        if (question.CommentPrompt is not null) yield return answer?.TextValue ?? string.Empty;
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

    /// <summary>Collapses newlines to a space and escapes backslashes before the column separator.</summary>
    private static string Escape(string value) => value
        .Replace("\r\n", " ", StringComparison.Ordinal)
        .Replace('\r', ' ')
        .Replace('\n', ' ')
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("|", "\\|", StringComparison.Ordinal);
}
