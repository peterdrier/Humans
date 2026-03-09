namespace Humans.Domain.Enums;

/// <summary>
/// What action to take during a sync operation.
/// Used as a parameter in sync methods — not persisted.
/// </summary>
public enum SyncAction
{
    /// <summary>Compute diff only, make no changes.</summary>
    Preview = 0,
    /// <summary>Compute diff and execute adds only.</summary>
    AddOnly = 1,
    /// <summary>Compute diff and execute adds + removes.</summary>
    AddAndRemove = 2
}
