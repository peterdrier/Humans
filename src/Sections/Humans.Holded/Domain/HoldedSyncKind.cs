namespace Humans.Holded.Domain;

/// <summary>One state row per kind, created on first use: there is nothing to seed.</summary>
internal enum HoldedSyncKind { Ledger, Accounts, FullSync }
