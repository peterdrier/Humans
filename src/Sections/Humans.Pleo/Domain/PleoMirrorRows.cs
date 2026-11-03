using NodaTime;

namespace Humans.Pleo.Domain;

/// <summary>A Pleo cardholder, by Pleo id only. Not bound to a member; contact fields are not mirrored.</summary>
internal sealed class PleoEmployee
{
    public string Id { get; init; } = "";
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public string? Code { get; set; }
    public bool IsDeleted { get; set; }
    public Instant SyncedAt { get; set; }

    public string DisplayName => $"{FirstName.Trim()} {LastName.Trim()}".Trim();
}

/// <summary>Pleo's chart of accounts, which is Holded's: <see cref="Code"/> is the Holded account number.</summary>
internal sealed class PleoAccount
{
    public string Id { get; init; } = "";
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? ExternalId { get; set; }
    public bool Archived { get; set; }
    public Instant SyncedAt { get; set; }
}

/// <summary>The wallet balance at the last sweep. One row.</summary>
internal sealed class PleoBalance
{
    public const int SingletonId = 1;

    public int Id { get; init; } = SingletonId;
    public string Currency { get; set; } = "";
    public long CurrentMinors { get; set; }
    public long AvailableMinors { get; set; }
    public long PendingDebitsMinors { get; set; }
    public Instant SyncedAt { get; set; }
}

internal enum PleoSyncStatus { Idle, Running, Error }

/// <summary>One row; no seed, so a never-run sync has no state.</summary>
internal sealed class PleoSyncState
{
    public const int SingletonId = 1;

    public int Id { get; init; } = SingletonId;
    public PleoSyncStatus SyncStatus { get; set; } = PleoSyncStatus.Idle;
    public Instant? LastSyncAt { get; set; }
    public Instant? StatusChangedAt { get; set; }
    public int LastCount { get; set; }

    /// <summary>Entries the API returned with no id or family, which are not mirrored.</summary>
    public int SkippedCount { get; set; }

    public string? LastError { get; set; }
}

/// <summary>
/// Pleo category → Holded account. Not a cache: the sweep never touches it. The name and code are copied from Pleo's
/// settings page; the id is linked by hand on /Pleo/Categories, because the API only ever returns the id.
/// </summary>
internal sealed class PleoCategory
{
    public Guid Id { get; init; }
    public string Name { get; set; } = "";

    /// <summary>Matched to <see cref="PleoAccount.Code"/>; a code missing from the chart is shown, not dropped.</summary>
    public string HoldedCode { get; set; } = "";

    public string? PleoCategoryId { get; set; }
    public Guid? SetByUserId { get; set; }
    public Instant? SetAt { get; set; }
}
