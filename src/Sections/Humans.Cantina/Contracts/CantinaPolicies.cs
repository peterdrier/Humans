namespace Humans.Cantina.Contracts;

/// <summary>Cantina's authorization policies, registered by its <c>Section</c>. Read admits Board; Manage never does.</summary>
public static class CantinaPolicies
{
    /// <summary>Cantina Admin, Board or Admin: the weekly and daily roster and the audited health reveal.</summary>
    public const string Read = "CantinaRead";

    /// <summary>Cantina Admin or Admin: the roster CSV downloads.</summary>
    public const string Manage = "CantinaManage";
}
