using System.Text.Json;

namespace Humans.Workgroups.Data.Configurations;

/// <summary>One shared options instance, so a stored document never depends on which configuration wrote it.</summary>
internal static class WorkgroupsJson
{
    public static readonly JsonSerializerOptions Options = new();
}
