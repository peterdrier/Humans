using Humans.Surveys.Contracts;
using System.Globalization;
using Humans.Base.Csv;
using Humans.Base.Extensions;

namespace Humans.Surveys.Models;

/// <summary>
/// One row per response. Headers are resolved prompts, disambiguated against metadata and other question headers.
/// Choice cells use stable option <b>values</b> (<c>value|value</c>), not labels, so the export joins against the
/// question schema.
/// </summary>
internal static class SurveyCsvExportBuilder
{
    public static byte[] Build(SurveyResponseExport export)
    {
        ArgumentNullException.ThrowIfNull(export);

        return HumansCsv.WriteBytes(csv =>
        {
            var header = new List<object?> { "response_id", "anonymity", "input_method", "submitted_at", "user_id", "user_name" };
            var used = header.Cast<string>().Select(HeaderKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
            header.AddRange(export.Questions.SelectMany(q =>
                SurveyResponsesMarkdownBuilder.ColumnHeaders(q).Select((h, i) => (object?)(i == 0 ? QuestionHeader(q, export.Questions, used) : h))));
            csv.WriteRow(header.ToArray());

            foreach (var row in export.Rows)
            {
                var byQuestion = row.Answers.ToDictionary(a => a.QuestionId);

                var cells = new List<object?>
                {
                    row.ResponseId,
                    row.Anonymity,
                    row.InputMethod,
                    row.SubmittedAt.ToIso8601() ?? string.Empty,
                    row.UserId,            // blank for non-Identified rows (null → empty cell)
                    row.UserName,
                };

                foreach (var q in export.Questions)
                {
                    cells.AddRange(SurveyResponsesMarkdownBuilder.Cells(q, byQuestion.GetValueOrDefault(q.QuestionId)));
                }

                csv.WriteRow(cells.ToArray());
            }
        });
    }

    private static string QuestionHeader(
        SurveyExportQuestion question, IReadOnlyList<SurveyExportQuestion> all, HashSet<string> used)
    {
        var key = HeaderKey(question.Prompt);
        var duplicate = all.Count(q => string.Equals(HeaderKey(q.Prompt), key, StringComparison.OrdinalIgnoreCase)) > 1;
        if (!duplicate && used.Add(key)) return question.Prompt;

        var shortId = question.QuestionId.ToString("N", CultureInfo.InvariantCulture)[..8];
        var stem = $"{question.Prompt} ({shortId})";
        var candidate = stem;
        for (var suffix = 2; !used.Add(HeaderKey(candidate)); suffix++)
            candidate = $"{stem} ({suffix.ToString(CultureInfo.InvariantCulture)})";
        return candidate;
    }

    // Shared CSV readers trim and ignore case; injection escaping can add a leading apostrophe.
    private static string HeaderKey(string header) => header.Trim().TrimStart('\'').Trim();
}
