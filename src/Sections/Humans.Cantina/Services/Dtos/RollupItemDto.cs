namespace Humans.Cantina.Services.Dtos;

/// <summary>
/// One row of an allergy / intolerance roll-up: the canonical chip label and
/// the count of on-site humans who checked that chip. Used for both roll-ups
/// on both surfaces — the count is over the week's unique cohort on the
/// weekly payload, and over the day's cohort on the daily one.
/// </summary>
internal sealed record RollupItemDto(string Label, int Count);
