namespace Humans.Holded.Domain;

/// <summary>One state row per kind, created on first use: there is nothing to seed. <see cref="Usage"/> is the
/// month's call count as Holded last reported it: <c>LastCount</c> is the count, <c>LastSyncAt</c> when it was read.</summary>
internal enum HoldedSyncKind { Ledger, Accounts, FullSync, PurchaseDocs, Contacts, BankMovements, Usage }
