namespace Humans.Cantina.Services.Dtos;

/// <summary>Weekly only, counted over the week's unique cohort.</summary>
internal sealed record RollupItemDto(string Label, int Count);
