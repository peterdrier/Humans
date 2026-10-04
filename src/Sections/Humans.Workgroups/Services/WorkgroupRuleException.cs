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
    public WorkgroupRuleException() : base("A workgroup rule rejected the operation.") { }
    public WorkgroupRuleException(string key) : this(key, args: []) { }
    public WorkgroupRuleException(string key, Exception inner)
        : base($"Workgroup rule '{key}' rejected the operation.", inner)
    {
        Key = key;
    }

    public WorkgroupRuleException(string key, params object[] args)
        : base($"Workgroup rule '{key}' rejected the operation.")
    {
        Key = key;
        Args = args;
    }

    /// <summary>The stable localization key, independent of the diagnostic message.</summary>
    public string Key { get; } = string.Empty;

    public object[] Args { get; } = [];
}
