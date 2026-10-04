using Humans.Surveys.Contracts;
using System.Globalization;
using Humans.Base.Csv;
using Humans.Surveys.Services;
using NodaTime.Text;

namespace Humans.Surveys.Models;

/// <summary>
/// Builds the per-response CSV download from a <see cref="SurveyResponseExport"/>. One row per response;
/// leading identity columns then one column per question (header = resolved prompt, disambiguated with
/// the short question id when prompts collide). Choice cells use the stable option <b>values</b>
/// (<c>value|value</c>) — not labels — so the export joins cleanly against the question schema for
/// analysis. Quoting, injection escaping, and invariant formatting come from the shared
/// <see cref="HumansCsv"/> conventions.
/// </summary>
internal static class SurveyCsvExportBuilder
{
    private static readonly InstantPattern SubmittedPattern = InstantPattern.General; // ISO-8601 UTC, invariant.

    public static byte[] Build(SurveyResponseExport export)
    {
        ArgumentNullException.ThrowIfNull(export);

        return HumansCsv.WriteBytes(csv =>
        {
            var header = new List<object?> { "response_id", "anonymity", "input_method", "submitted_at", "user_id", "user_name" };
            header.AddRange(export.Questions.Select(q => (object?)QuestionHeader(q, export.Questions)));
            csv.WriteRow(header.ToArray());

            foreach (var row in export.Rows)
            {
                var byQuestion = row.Answers.ToDictionary(a => a.QuestionId);

                var cells = new List<object?>
                {
                    row.ResponseId,
                    row.Anonymity,
                    row.InputMethod,
                    row.SubmittedAt is { } at ? SubmittedPattern.Format(at) : string.Empty,
                    row.UserId,            // blank for non-Identified rows (null → empty cell)
                    row.UserName,
                };

                foreach (var q in export.Questions)
                {
                    cells.Add(byQuestion.TryGetValue(q.QuestionId, out var answer) ? SurveyResponsesMarkdownBuilder.CellValue(q, answer) : string.Empty);
                }

                csv.WriteRow(cells.ToArray());
            }
        });
    }

    /// <summary>Resolved prompt, suffixed with the short question id only when another question shares the same prompt.</summary>
    private static string QuestionHeader(SurveyExportQuestion question, IReadOnlyList<SurveyExportQuestion> all)
    {
        var duplicate = all.Count(q => string.Equals(q.Prompt, question.Prompt, StringComparison.Ordinal)) > 1;
        if (!duplicate) return question.Prompt;

        var shortId = question.QuestionId.ToString("N", CultureInfo.InvariantCulture)[..8];
        return $"{question.Prompt} ({shortId})";
    }
}
