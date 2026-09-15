namespace Humans.Users.Models;

/// <summary>One account on the other end of a merge, named where the row is still there.</summary>
internal sealed class MergedAccountViewModel
{
    public Guid UserId { get; init; }
    public string? DisplayName { get; init; }
    public DateTime? MergedAt { get; init; }
}

/// <summary>Both directions of the merge graph around one account.</summary>
internal sealed class MergedAccountsViewModel
{
    /// <summary>The account this one was folded into, when this one is a tombstone.</summary>
    public MergedAccountViewModel? MergedInto { get; init; }

    /// <summary>When this account was folded away.</summary>
    public DateTime? MergedAt { get; init; }

    /// <summary>Every account folded into this one, transitively.</summary>
    public IReadOnlyList<MergedAccountViewModel> MergedIn { get; init; } = [];
}
