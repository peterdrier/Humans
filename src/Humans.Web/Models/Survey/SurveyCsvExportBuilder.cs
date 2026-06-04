using System.Globalization;
using System.Text;
using Humans.Application.Interfaces.Surveys;
using Humans.Domain.Enums;
using Humans.Web.Extensions;
using NodaTime.Text;

namespace Humans.Web.Models.Survey;

/// <summary>
/// Builds the per-response CSV download from a <see cref="SurveyResponseExport"/>. One row per response;
/// leading identity columns then one column per question (header = resolved prompt, disambiguated with
/// the short question id when prompts collide). Choice cells use the stable option <b>values</b>
/// (<c>value|value</c>) — not labels — so the export joins cleanly against the question schema for
/// analysis. Field escaping is RFC4180 (every field quoted, embedded quotes doubled) via
/// <see cref="CsvExtensions.AppendCsvRow"/>; numbers/instants format with <see cref="CultureInfo.InvariantCulture"/>.
/// </summary>
public static class SurveyCsvExportBuilder
{
    private static readonly InstantPattern SubmittedPattern = InstantPattern.General; // ISO-8601 UTC, invariant.

    public static byte[] Build(SurveyResponseExport export)
    {
        ArgumentNullException.ThrowIfNull(export);

        var csv = new StringBuilder();

        // Header: fixed identity columns + one disambiguated column per question.
        var header = new List<object?> { "response_id", "anonymity", "input_method", "submitted_at", "user_id", "user_name" };
        header.AddRange(export.Questions.Select(q => (object?)QuestionHeader(q, export.Questions)));
        csv.AppendCsvRow(header.ToArray());

        foreach (var row in export.Rows)
        {
            var byQuestion = row.Answers.ToDictionary(a => a.QuestionId);

            var cells = new List<object?>
            {
                row.ResponseId,
                row.Anonymity,
                row.InputMethod,
                row.SubmittedAt is { } at ? SubmittedPattern.Format(at) : string.Empty,
                row.UserId,            // blank for non-Identified rows (null → empty by AppendCsvRow)
                row.UserName,          // blank for non-Identified rows
            };

            foreach (var q in export.Questions)
            {
                cells.Add(byQuestion.TryGetValue(q.QuestionId, out var answer) ? CellValue(q, answer) : string.Empty);
            }

            csv.AppendCsvRow(cells.ToArray());
        }

        return Encoding.UTF8.GetBytes(csv.ToString());
    }

    /// <summary>The cell content for one answer: choice values flattened <c>a|b</c>, free text verbatim, or the rating integer.</summary>
    private static string CellValue(SurveyExportQuestion question, SurveyExportAnswer answer) => question.Type switch
    {
        SurveyQuestionType.SingleChoice or SurveyQuestionType.MultiChoice =>
            string.Join("|", answer.SelectedValues),
        SurveyQuestionType.Rating =>
            answer.RatingValue?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
        _ => answer.TextValue ?? string.Empty,
    };

    /// <summary>Resolved prompt, suffixed with the short question id only when another question shares the same prompt.</summary>
    private static string QuestionHeader(SurveyExportQuestion question, IReadOnlyList<SurveyExportQuestion> all)
    {
        var duplicate = all.Count(q => string.Equals(q.Prompt, question.Prompt, StringComparison.Ordinal)) > 1;
        if (!duplicate) return question.Prompt;

        var shortId = question.QuestionId.ToString("N", CultureInfo.InvariantCulture)[..8];
        return $"{question.Prompt} ({shortId})";
    }
}
