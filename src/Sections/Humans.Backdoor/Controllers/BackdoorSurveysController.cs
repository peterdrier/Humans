using System.Globalization;
using System.Text;
using Humans.Backdoor.Filters;
using Humans.Base.Controllers;
using Humans.Surveys.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using NodaTime.Text;

namespace Humans.Backdoor.Controllers;

/// <summary>
/// Read-only survey analysis for agents.
/// </summary>
/// <remarks>
/// Every endpoint reuses an <see cref="ISurveyAnalysisRead"/> method; the controller only
/// parses, filters, sorts, pages and formats (hard rule). Identity columns surface only for
/// <see cref="ResponseAnonymity.Identified"/> rows — guaranteed server-side by
/// <see cref="ISurveyAnalysisRead.GetResponseExportAsync"/>, never re-derived here.
/// </remarks>
[ApiController]
[Route("api/backdoor/surveys")]
[ServiceFilter(typeof(BackdoorApiKeyAuthFilter))]
internal sealed class BackdoorSurveysController(ISurveyAnalysisRead surveys, IUserServiceRead users)
    : ApiControllerBase(users)
{
    private const int DefaultLimit = 100;
    private const int MaxLimit = 1000;
    private static readonly InstantPattern SubmittedPattern = InstantPattern.General; // ISO-8601 UTC, invariant.

    /// <summary>All surveys with participation counts.</summary>
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var summaries = await surveys.GetSummariesAsync(ct);
        return Ok(summaries.Select(s => new
        {
            id = s.Id,
            title = s.Title,
            status = s.Status.ToString(),
            invitedCount = s.InvitedCount,
            responseCount = s.ResponseCount,
        }));
    }

    /// <summary>A survey's definition: status, default culture, and the ordered question graph (with branching + options).</summary>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Definition(Guid id, CancellationToken ct)
    {
        var definition = await surveys.GetDefinitionAsync(id, ct);
        if (definition is null) return NotFound();

        return Ok(new
        {
            id = definition.Id,
            title = definition.Title,
            status = definition.Status.ToString(),
            defaultCulture = definition.DefaultCulture,
            questions = definition.Questions.Select(q => new
            {
                id = q.Id,
                page = q.Page,
                order = q.Order,
                type = q.Type.ToString(),
                prompt = q.Prompt,
                markdown = q.Markdown,
                required = q.Required,
                ratingMin = q.RatingMin,
                ratingMax = q.RatingMax,
                showIf = q.ShowIf,   // structural condition; option values are the join keys
                gridSelectionMode = q.GridSelectionMode?.ToString(),
                gridRows = q.GridRows.Select(row => new { value = row.Value, label = row.Label }),
                images = q.Images.Select(image => new
                {
                    id = image.Id,
                    url = image.Url,
                    label = image.Label,
                    altText = image.AltText,
                }),
                options = q.Options.Select(o => new { value = o.Value, label = o.Label }),
            }),
        });
    }

    /// <summary>
    /// Raw per-response export, filtered/paged in the controller. JSON (default) returns
    /// <c>{ items, nextCursor }</c>; <c>?format=md</c> returns the bulk as a Markdown table.
    /// </summary>
    [HttpGet("{id:guid}/responses")]
    public async Task<IActionResult> Responses(
        Guid id,
        [FromQuery] ResponseAnonymity? anonymity,
        [FromQuery] string? since,
        [FromQuery] int limit = DefaultLimit,
        [FromQuery] string? cursor = null,
        [FromQuery] string? format = null,
        CancellationToken ct = default)
    {
        var export = await surveys.GetResponseExportAsync(id, ct);
        if (export is null) return NotFound();

        Instant? sinceInstant = null;
        if (!string.IsNullOrWhiteSpace(since))
        {
            var parsed = SubmittedPattern.Parse(since);
            if (!parsed.Success) return BadRequest(new { error = "Invalid 'since' — expected an ISO-8601 instant." });
            sinceInstant = parsed.Value;
        }

        // Rows arrive ordered by SubmittedAt — the cursor below relies on it.
        var filtered = export.Rows
            .Where(r => anonymity is null || r.Anonymity == anonymity)
            .Where(r => sinceInstant is null || (r.SubmittedAt is { } at && at >= sinceInstant))
            .ToList();

        if (string.Equals(format, "md", StringComparison.OrdinalIgnoreCase))
        {
            var md = SurveyResponsesMarkdownBuilder.Build(export.Questions, filtered);
            return Content(md, "text/markdown", Encoding.UTF8);
        }

        // Offset-based opaque cursor over the filtered, time-ordered rows.
        var offset = DecodeCursor(cursor);
        var pageSize = Math.Clamp(limit, 1, MaxLimit);
        var page = filtered.Skip(offset).Take(pageSize).ToList();
        var nextOffset = offset + page.Count;
        var nextCursor = nextOffset < filtered.Count ? EncodeCursor(nextOffset) : null;

        return Ok(new
        {
            items = page.Select(r => MapRow(export.Questions, r)),
            nextCursor,
        });
    }

    /// <summary>Per-question aggregates plus the participation funnel.</summary>
    [HttpGet("{id:guid}/aggregates")]
    public async Task<IActionResult> Aggregates(Guid id, CancellationToken ct)
    {
        var results = await surveys.GetResultsAsync(id, ct);
        if (results is null) return NotFound();

        return Ok(new
        {
            id = results.SurveyId,
            title = results.Title,
            status = results.Status.ToString(),
            invitedCount = results.InvitedCount,
            responseCount = results.ResponseCount,
            responseRate = results.ResponseRate,
            funnel = new
            {
                linkStarted = results.Funnel.LinkStarted,
                linkFinished = results.Funnel.LinkFinished,
                slugStarted = results.Funnel.SlugStarted,
                slugFinished = results.Funnel.SlugFinished,
            },
            questions = results.Questions.Select(q => new
            {
                id = q.QuestionId,
                prompt = q.Prompt,
                type = q.Type.ToString(),
                optionCounts = q.OptionCounts.Select(o => new { value = o.Value, label = o.Label, count = o.Count, percent = o.Percent }),
                grid = q.Grid is null
                    ? null
                    : new
                    {
                        mode = q.Grid.Mode.ToString(),
                        columns = q.Grid.Columns.Select(column => new { value = column.Value, label = column.Label }),
                        rows = q.Grid.Rows.Select(row => new
                        {
                            value = row.Value,
                            label = row.Label,
                            cells = row.Cells.Select(cell => new
                            {
                                columnValue = cell.ColumnValue,
                                columnLabel = cell.ColumnLabel,
                                count = cell.Count,
                                percent = cell.Percent,
                            }),
                        }),
                    },
                ratingDistribution = q.RatingDistribution.Select(b => new { value = b.Value, count = b.Count }),
                ratingAverage = q.RatingAverage,
                freeTextAnswers = q.FreeTextAnswers,
            }),
        });
    }

    private static object MapRow(IReadOnlyList<SurveyExportQuestion> questions, SurveyExportRow row)
    {
        var byQuestion = row.Answers.ToDictionary(a => a.QuestionId);
        return new
        {
            responseId = row.ResponseId,
            anonymity = row.Anonymity.ToString(),
            inputMethod = row.InputMethod.ToString(),
            culture = row.Culture,
            submittedAt = row.SubmittedAt is { } at ? SubmittedPattern.Format(at) : null,
            userId = row.UserId,        // null for non-Identified rows (enforced by the export DTO)
            userName = row.UserName,    // null for non-Identified rows
            answers = questions
                .Where(q => byQuestion.ContainsKey(q.QuestionId))
                .Select(q =>
                {
                    var a = byQuestion[q.QuestionId];
                    return new
                    {
                        questionId = q.QuestionId,
                        selectedValues = a.SelectedValues,
                        selectedLabels = a.SelectedLabels,
                        gridSelections = a.GridSelections,
                        gridSelectionLabels = a.GridSelectionLabels?.Select(selection => new
                        {
                            rowValue = selection.RowValue,
                            rowLabel = selection.RowLabel,
                            columnValues = selection.ColumnValues,
                            columnLabels = selection.ColumnLabels,
                        }),
                        textValue = a.TextValue,
                        ratingValue = a.RatingValue,
                    };
                }),
        };
    }

    private static int DecodeCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return 0;
        try
        {
            var raw = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var offset) && offset >= 0
                ? offset
                : 0;
        }
        catch (FormatException)
        {
            return 0;
        }
    }

    private static string EncodeCursor(int offset) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(offset.ToString(CultureInfo.InvariantCulture)));
}
