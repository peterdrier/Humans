namespace Humans.Backdoor.Services;

/// <summary>
/// Who a key-authed controller serves. A person may hold a key when any scope admits them, and a key reaches only the
/// controllers whose scope admits its owner; the rest answer 403. A new scope is a value here plus its test in
/// <see cref="BackdoorApiKeyService.AdmitsAsync"/>.
/// </summary>
internal enum BackdoorScope
{
    /// <summary>A full Admin or a Board member.</summary>
    AdminAndBoard,

    /// <summary>Holds <c>Tasks.Manage</c> on some team, which TasksAdmin and Admin do everywhere.</summary>
    Tasks,
}

/// <summary>Required on every key-authed controller: <c>BackdoorApiKeyAuthFilter</c> refuses an action without one.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
internal sealed class BackdoorScopeAttribute(BackdoorScope scope) : Attribute
{
    public BackdoorScope Scope { get; } = scope;
}
