namespace Humans.Users.Domain;

/// <summary>Frozen storage and export values; names and numeric values must stay stable until the column is dropped.</summary>
internal enum LegacyMembershipTier
{
    Volunteer = 0,
    Colaborador = 1,
    Asociado = 2
}
