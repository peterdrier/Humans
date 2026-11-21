namespace Humans.Base.Demo;

/// <summary>Names a demo story; other stories list it in <see cref="IDemoStory.After"/>.</summary>
public readonly record struct DemoStoryKey(string Value)
{
    public override string ToString() => Value;
}

/// <summary>
/// A section's part of the non-production demo world, written through the section's own services acting as cast
/// members. Never registered in Production (<see cref="DemoRegistration"/>).
/// </summary>
public interface IDemoStory
{
    DemoStoryKey Key { get; }

    /// <summary>Stories whose rows this one needs; they tell first and retire last.</summary>
    IReadOnlyList<DemoStoryKey> After { get; }

    /// <summary>Adds what is missing and repairs demo rows that drifted. Must be safe to run again.</summary>
    Task TellAsync(IDemoWorld world, CancellationToken ct);

    /// <summary>
    /// Ends what this story made in the current generation through actions the app already has (cancel, withdraw,
    /// deactivate, the non-production account purge); never through a delete verb added for the demo. What cannot
    /// be ended is marked left behind. Runs after a top-up, so nothing this story began is still half-made.
    /// </summary>
    Task RetireAsync(IDemoWorld world, CancellationToken ct);
}

/// <summary>One demo run's view of the ledger of rows the demo made, scoped to the current generation.</summary>
public interface IDemoWorld
{
    int Generation { get; }

    /// <summary>The admin who started the run.</summary>
    Guid ActorUserId { get; }

    /// <summary>
    /// Returns the ledger's row for <paramref name="key"/> when <paramref name="exists"/> still finds it; otherwise
    /// runs <paramref name="create"/> with a planned id (a verb that takes an id should use it) and records the id
    /// it returns. A run cut off mid-create calls <paramref name="create"/> again with the same id, so it must finish
    /// a row it half-made rather than fail on it.
    /// </summary>
    Task<Guid> EnsureAsync(
        string key,
        Func<Guid, CancellationToken, Task<bool>> exists,
        Func<Guid, CancellationToken, Task<Guid>> create,
        CancellationToken ct);

    /// <summary>Rows made in this generation whose key starts with <paramref name="keyPrefix"/>, by key.</summary>
    Task<IReadOnlyDictionary<string, Guid>> MadeAsync(string keyPrefix, CancellationToken ct);

    Task MarkRetiredAsync(string key, CancellationToken ct);

    /// <summary>Records that the row outlives a reset, and why; the rebuild leaves it alone.</summary>
    Task MarkLeftBehindAsync(string key, string reason, CancellationToken ct);

    /// <summary>A line for the run report the admin reads.</summary>
    void Report(string line);
}

/// <summary>
/// A section's veto on running the demo here, for example because its outbound integration would push demo people
/// into a real external system. Returns null when it is safe.
/// </summary>
public interface IDemoSafetyCheck
{
    Task<string?> RefusalAsync(CancellationToken ct);
}
