namespace Humans.Grants.Domain;

/// <summary>Closing never touches an issued grant.</summary>
internal enum GrantProgramStatus
{
    Open = 0,
    Closed = 1,
}

internal enum GrantIssuer
{
    /// <summary>Managers pick people on the program page.</summary>
    Managers = 0,

    /// <summary>Another section issues through <c>IGrantWrite</c>. Managers still see the grants and may revoke.</summary>
    Source = 1,
}
