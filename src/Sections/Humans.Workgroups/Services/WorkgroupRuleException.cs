namespace Humans.Workgroups.Services;

/// <summary>
/// A rule the human can act on (the register is frozen, the last coordinator needs a
/// replacement, the root Drive folder is not configured yet). <see cref="Key"/> is a
/// <see cref="WorkgroupsResource"/> key; the controller localizes it with
/// <see cref="Args"/>. Derives from <see cref="InvalidOperationException"/> so the
/// service's error contract stays a single family.
/// </summary>
internal sealed class WorkgroupRuleException : InvalidOperationException
{
    public WorkgroupRuleException() { }
    public WorkgroupRuleException(string message) : base(message) { }
    public WorkgroupRuleException(string message, Exception inner) : base(message, inner) { }

    public WorkgroupRuleException(string key, params object[] args) : base(key)
    {
        Args = args;
    }

    /// <summary>The <see cref="WorkgroupsResource"/> key (the message as thrown).</summary>
    public string Key => Message;

    public object[] Args { get; } = [];
}
