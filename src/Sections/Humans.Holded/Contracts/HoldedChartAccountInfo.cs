namespace Humans.Holded.Contracts;

/// <summary>One account of Holded's chart, from the mirror. <paramref name="Name"/> is
/// Holded's own (Spanish) text; <paramref name="EnglishName"/> is the standard PGC name in English, null when the
/// name is one the association chose. <paramref name="HoldedBalance"/> is Holded's Σdebit − Σcredit, raw sign. No
/// Holded id crosses this contract: the number is the identity.</summary>
public sealed record HoldedChartAccountInfo(
    int Number, string Name, string? EnglishName, bool Archived, decimal HoldedBalance);
