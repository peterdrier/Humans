using System.Globalization;
using System.Text;
using Humans.Application.Interfaces.Surveys;
using Humans.Domain.Enums;
using NodaTime.Text;

namespace Humans.Web.Models.Survey;

/// <summary>
/// Renders an already-filtered set of <see cref="SurveyExportRow"/>s as a GitHub-flavoured Markdown
/// table — the token-lean shape an agent reads when scanning the bulk of a survey's responses. Columns
/// mirror <see cref="SurveyCsvExportBuilder"/>: leading <c>anonymity</c> + <c>input_method</c> + identity
/// (<c>user_name</c>, populated only for Identified rows — already enforced by the export DTO), then one
/// column per question (header = resolved prompt). Choice cells flatten to the stable option
/// <b>values</b> (<c>a|b</c>), not labels, so the table joins cleanly against the definition endpoint.
/// Cells escape Markdown table specials: newlines collapse to a space and <c>|</c> becomes <c>\|</c>.
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
                byQuestion.TryGetValue(q.QuestionId, out var answer) ? CellValue(q, answer) : string.Empty));

            sb.Append("| ").Append(string.Join(" | ", cells)).Append(" |\n");
        }

        return sb.ToString();
    }

    /// <summary>The cell content for one answer: choice values flattened <c>a|b</c>, free text verbatim, or the rating integer.</summary>
    private static string CellValue(SurveyExportQuestion question, SurveyExportAnswer answer) => question.Type switch
    {
        SurveyQuestionType.SingleChoice or SurveyQuestionType.MultiChoice =>
            Escape(string.Join("|", answer.SelectedValues)),
        SurveyQuestionType.Rating =>
            answer.RatingValue?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
        _ => Escape(answer.TextValue ?? string.Empty),
    };

    /// <summary>Markdown-table-safe cell: collapse newlines to a space, escape the column separator.</summary>
    private static string Escape(string value) => value
        .Replace("\r\n", " ", StringComparison.Ordinal)
        .Replace('\r', ' ')
        .Replace('\n', ' ')
        .Replace("|", "\\|", StringComparison.Ordinal);
}
