using System.Reflection;
using Humans.Base.Interfaces;
using Humans.Debug.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Debug;

/// <summary>
/// Debug's endpoint contribution: <c>GET /api/data-migrations</c>, the anonymous status of every
/// registered data migration, polled by development sessions to learn whether production has
/// finished a copy before its retirement PR. Keys, pending counts and a done flag only — never
/// row data (Docs/Debug.md, "Data migrations").
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

/// <summary>The <c>/api/data-migrations</c> body.</summary>
internal sealed record DataMigrationsStatusResponse(string Version, IReadOnlyList<DataMigrationProgress> Migrations);

/// <summary>One migration's published progress. Counts only — never row data.</summary>
internal sealed record DataMigrationProgress(string Key, int Pending, bool Done);
