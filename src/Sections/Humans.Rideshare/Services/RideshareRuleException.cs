namespace Humans.Rideshare.Services;

/// <summary>
/// A rule the human can act on (not enough seats, place not found, year not set up).
/// <see cref="Key"/> is a <see cref="RideshareResource"/> key; the controller localizes it
/// with <see cref="Args"/>. Derives from <see cref="InvalidOperationException"/> so the
/// service's error contract stays a single family.
/// </summary>
internal sealed class RideshareRuleException : InvalidOperationException
{
    public RideshareRuleException() { }
    public RideshareRuleException(string message) : base(message) { }
    public RideshareRuleException(string message, Exception inner) : base(message, inner) { }

    public RideshareRuleException(string key, params object[] args) : base(key)
    {
        Args = args;
    }

    /// <summary>The <see cref="RideshareResource"/> key (the message as thrown).</summary>
    public string Key => Message;

    public object[] Args { get; } = [];
}
