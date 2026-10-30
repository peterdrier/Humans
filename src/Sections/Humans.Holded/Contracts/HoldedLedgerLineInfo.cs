using NodaTime;

namespace Humans.Holded.Contracts;

/// <summary>A copy of what Holded returned for the line, never a local judgement. <paramref name="EntryId"/> is
/// Holded's journal-entry id (shared by every line of the entry); <paramref name="Checked"/> is Holded's manual
/// ledger tick, not bank reconciliation.</summary>
public sealed record HoldedLedgerLineInfo(
    int EntryNumber, int Line, int AccountNum, Instant Date,
    string? Type, string? Description, decimal Debit, decimal Credit,
    string? EntryId = null, bool Checked = false);
