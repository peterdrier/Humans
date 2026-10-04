namespace Humans.Rideshare.Services;

/// <summary>
/// A rule the human can act on (not enough seats, place not found, year not set up).
/// <see cref="Key"/> is a <see cref="RideshareResource"/> key; the controller localizes it
/// with <see cref="Args"/>. Derives from <see cref="InvalidOperationException"/> so the
/// service's error contract stays a single family.
/// </summary>
internal sealed class RideshareRuleException : InvalidOperationException
{
    public RideshareRuleException() : base("A rideshare rule rejected the operation.") { }
    public RideshareRuleException(string key) : this(key, args: []) { }
    public RideshareRuleException(string key, Exception inner)
        : base($"Rideshare rule '{key}' rejected the operation.", inner)
    {
        Key = key;
    }

    public RideshareRuleException(string key, params object[] args)
        : base($"Rideshare rule '{key}' rejected the operation.")
    {
        Key = key;
        Args = args;
    }

    /// <summary>The stable localization key, independent of the diagnostic message.</summary>
    public string Key { get; } = string.Empty;

    public object[] Args { get; } = [];
}
