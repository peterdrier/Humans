namespace Humans.Governance.Contracts;

/// <summary>Stored by name: never rename a value.</summary>
public enum MembershipTier
{
    /// <summary>The default. No application.</summary>
    Volunteer = 0,

    /// <summary>Application plus Board vote; 2-year term ending Dec 31 of an odd year.</summary>
    Colaborador = 1,

    /// <summary>Votes in assemblies and elections. Application plus Board vote; 2-year term ending Dec 31 of an odd year.</summary>
    Asociado = 2
}
