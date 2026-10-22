using System.Reflection;
using Humans.Base.Interfaces;
using Humans.Debug.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Debug;

/// <summary>
/// Anonymous: development sessions poll it to learn whether production finished a copy before its retirement PR. Keys,
/// pending counts and done flags only, never row data.
/// </summary>
internal sealed class SectionEndpoints : ISectionEndpoints
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/api/data-migrations", (RequestDelegate)WriteStatusAsync).AllowAnonymous();

    internal static async Task WriteStatusAsync(HttpContext http)
    {
        var reports = await http.RequestServices.GetRequiredService<DataMigrationService>()
            .GetReportsAsync(http.RequestAborted);

        var response = new DataMigrationsStatusResponse(
            AppVersion(),
            [.. reports.Select(r => new DataMigrationProgress(r.Migration.Key, r.Status.Pending, r.Status.Done))]);

        await http.Response.WriteAsJsonAsync(response, http.RequestAborted);
    }

    /// <summary>The same <c>version</c> <c>/api/version</c> reports: the informational version without the commit.</summary>
    private static string AppVersion()
    {
        var informational = Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? string.Empty;
        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        return plus >= 0 ? informational[..plus] : informational;
    }
}

internal sealed record DataMigrationsStatusResponse(string Version, IReadOnlyList<DataMigrationProgress> Migrations);

internal sealed record DataMigrationProgress(string Key, int Pending, bool Done);
