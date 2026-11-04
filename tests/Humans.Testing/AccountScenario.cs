namespace Humans.Testing;

/// <summary>
/// The signed-in account a controller or handler resolves before acting: still current, merged into a
/// survivor, gone, or the request aborted during the lookup.
/// </summary>
public enum AccountScenario
{
    Current,
    Merged,
    Missing,
    Cancelled
}
