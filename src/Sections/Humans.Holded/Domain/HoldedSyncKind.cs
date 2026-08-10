namespace Humans.Holded.Domain;

/// <summary>
/// The sync each state row tracks. One row per kind, lazy-created on first use — the mirror's
/// state is bookkeeping, so there is nothing to seed.
/// </summary>
internal enum HoldedSyncKind { Ledger, Accounts, FullSync }
