namespace Humans.Application.Services.Cantina.Dtos;

/// <summary>
/// One row of an allergy / intolerance roll-up: the canonical chip label
/// and the count of on-site humans for the day who checked that chip.
/// Used for both the allergy and intolerance roll-ups on the Cantina
/// Daily Roster page (feature #36 — docs/features/cantina/daily-roster.md).
/// </summary>
public sealed record RollupItemDto(string Label, int Count);
