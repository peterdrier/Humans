namespace Humans.Grants.Domain;

/// <summary>Open takes new grants; Closed takes none. Closing never touches an issued grant.</summary>
internal enum GrantProgramStatus
{
    Open = 0,
    Closed = 1,
}

/// <summary>Who fills a program.</summary>
internal enum GrantIssuer
{
    /// <summary>The program's managers pick people on the program page (build, strike).</summary>
    Managers = 0,

    /// <summary>
    /// Another section issues through <c>IGrantWrite</c> under the program's
    /// <c>SourceKey</c> (Low Income). Managers still see the grants and may revoke.
    /// </summary>
    Source = 1,
}
