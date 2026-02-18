namespace Humans.Domain.Constants;

/// <summary>
/// Well-known IDs for system-managed teams.
/// </summary>
public static class SystemTeamIds
{
    public static readonly Guid Volunteers = Guid.Parse("00000000-0000-0000-0001-000000000001");
    public static readonly Guid Leads = Guid.Parse("00000000-0000-0000-0001-000000000002");
    public static readonly Guid Board = Guid.Parse("00000000-0000-0000-0001-000000000003");
    public static readonly Guid Asociados = Guid.Parse("00000000-0000-0000-0001-000000000004");
    public static readonly Guid Colaboradors = Guid.Parse("00000000-0000-0000-0001-000000000005");
}
