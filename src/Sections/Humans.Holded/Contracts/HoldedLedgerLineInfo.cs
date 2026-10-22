using NodaTime;

namespace Humans.Holded.Contracts;

/// <summary>A copy of what Holded returned for the line, never a local judgement.</summary>
public sealed record HoldedLedgerLineInfo(
    int EntryNumber, int Line, int AccountNum, Instant Date,
    string? Type, string? Description, decimal Debit, decimal Credit);
