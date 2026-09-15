using System.Text.Json;

namespace Humans.Workgroups.Data.Configurations;

/// <summary>
/// One shared <see cref="JsonSerializerOptions"/> for the section's jsonb columns, so a
/// stored document never depends on which configuration wrote it. Defaults on purpose:
/// the only jsonb column is a list of plain strings.
/// </summary>
internal static class WorkgroupsJson
{
    public static readonly JsonSerializerOptions Options = new();
}
