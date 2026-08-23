namespace Humans.Cantina.Services.Dtos;

/// <summary>
/// One row of an allergy / intolerance roll-up: the canonical chip label and
/// the count of on-site humans who checked that chip. Weekly payload only —
/// the count is over the week's unique cohort. The daily matrix carries no
/// roll-up members; its view and CSV writer count their own column totals
/// from the person rows.
/// </summary>
internal sealed record RollupItemDto(string Label, int Count);
