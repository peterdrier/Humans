using NodaTime;

namespace Humans.Holded.Contracts;

/// <summary>
/// One mirrored Holded journal line, as other sections see it. The mirror is re-derivable —
/// every field here is a copy of what Holded returned for the line, never a local judgement.
/// </summary>
public sealed record HoldedLedgerLineInfo(
    int EntryNumber, int Line, int AccountNum, Instant Date,
    string? Type, string? Description, decimal Debit, decimal Credit);
