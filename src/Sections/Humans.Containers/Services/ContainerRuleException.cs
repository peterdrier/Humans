namespace Humans.Containers.Services;

/// <summary>
/// An image rule the camp lead can act on. <see cref="Key"/> is a <see cref="ContainersResource"/>
/// key; the controller localizes it with <see cref="Args"/>. Derives from
/// <see cref="InvalidOperationException"/> so the service's error contract stays one family
/// (City Planning's admin page, exempt from localization, still shows the English message).
/// </summary>
internal sealed class ContainerRuleException : InvalidOperationException
{
    public ContainerRuleException() { }
    public ContainerRuleException(string message) : base(message) { }
    public ContainerRuleException(string message, Exception inner) : base(message, inner) { }

    public ContainerRuleException(string key, string englishMessage, params object[] args) : base(englishMessage)
    {
        Key = key;
        Args = args;
    }

    /// <summary>The <see cref="ContainersResource"/> key, or empty for the plain-message constructors.</summary>
    public string Key { get; } = string.Empty;

    public object[] Args { get; } = [];
}
